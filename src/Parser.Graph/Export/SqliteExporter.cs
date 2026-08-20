using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Parser.Graph;

/// <summary>
/// Writes the in-memory <see cref="GraphPayload"/> as a single queryable SQLite
/// database - the "query surface" export. Same deterministic facts as
/// graph_full.json / the node store, but in a form an agent/LLM queries with one
/// SQL statement (including transitive impact via a recursive CTE) instead of
/// scanning JSON files.
///
/// Schema:
///   nodes(id PK, label, name, cyclomatic_complexity, total_steps,
///         dynamic_sql_steps, unresolved_dynamic_sql_steps, max_nesting, db)
///   edges(src, dst, type, props)   -- props = JSON of the edge properties
///
/// unresolved_dynamic_sql_steps is the "no lo sé" signal for confidence buckets: it
/// counts EXEC steps where dynamic SQL never resolved to a literal, so the object's
/// READS_FROM/WRITES_TO/READS_COLUMN edges are a provable undercount, not a provable
/// empty set. See scripts/lineage-queries.sql @col_impact.
///
/// Per-object scalars (steps/dynamic/nesting) are rolled up here from Step nodes
/// so a corpus-wide report is a single query. READS_FROM/WRITES_TO edges stay at
/// Step granularity (a Step id is "&lt;objId&gt;#step&lt;N&gt;"); object-level
/// aggregation rolls the step up to its owner in the query (substr before '#') -
/// see scripts/lineage-queries.sql.
/// </summary>
public static class SqliteExporter
{
    public static void Write(GraphPayload graph, string dbPath, string database, string project)
    {
        // ── roll up step-level facts to the owning SqlObject ────────────────
        var totalSteps = new Dictionary<string, int>(StringComparer.Ordinal);
        var dynamicSteps = new Dictionary<string, int>(StringComparer.Ordinal);
        // Of the dynamic-SQL steps, how many never resolved to a literal (dynamic_sql
        // property empty/missing): these run real SQL at execution time that
        // READS_FROM/WRITES_TO/READS_COLUMN can't see - the parser fails closed rather
        // than guessing, so this is the "the engine is blind here" signal a confidence
        // consumer needs (the "No lo sé" bucket). Same criterion NodeStoreExporter uses
        // for model.json's unresolved_dynamic_sql_steps (NodeStoreExporter.cs), kept in
        // sync here rather than duplicated in GraphExporter since both read the same
        // Step node properties off graph.Nodes.
        var unresolvedDynamicSteps = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxNesting = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!n.Labels.Contains("Step"))
                continue;
            var hash = n.Id.IndexOf('#');
            if (hash <= 0)
                continue;
            var owner = n.Id[..hash];
            totalSteps[owner] = totalSteps.GetValueOrDefault(owner) + 1;
            var isDynamic = n.Properties.TryGetValue("is_dynamic_sql", out var d) && d is true;
            if (isDynamic)
            {
                dynamicSteps[owner] = dynamicSteps.GetValueOrDefault(owner) + 1;
                var resolved = n.Properties.TryGetValue("dynamic_sql", out var dsql) && dsql is string { Length: > 0 };
                if (!resolved)
                    unresolvedDynamicSteps[owner] = unresolvedDynamicSteps.GetValueOrDefault(owner) + 1;
            }
            if (n.Properties.TryGetValue("nesting_level", out var nl) && nl is int lvl)
                maxNesting[owner] = Math.Max(maxNesting.GetValueOrDefault(owner), lvl);
        }

        if (File.Exists(dbPath))
            File.Delete(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var schema = conn.CreateCommand())
        {
            schema.CommandText =
                """
                CREATE TABLE nodes(
                    id TEXT PRIMARY KEY, label TEXT, name TEXT, db TEXT,
                    -- rolled-up SqlObject scalars
                    cyclomatic_complexity INTEGER, total_steps INTEGER,
                    dynamic_sql_steps INTEGER, max_nesting INTEGER,
                    -- of dynamic_sql_steps, how many never resolved to a literal ("no lo sé" signal)
                    unresolved_dynamic_sql_steps INTEGER,
                    -- promoted SqlObject audit flags (robustness / inventory)
                    object_type TEXT, schema_name TEXT,
                    has_error_handling INTEGER, has_cursor INTEGER, has_transaction INTEGER,
                    -- promoted Step dimensions (security / operations / maintainability)
                    action TEXT, is_dynamic_sql INTEGER, nesting_level INTEGER,
                    -- promoted Column dimensions (schema governance)
                    data_type TEXT, is_nullable INTEGER, is_primary_key INTEGER,
                    -- full property bag (lossless): every property, incl. the promoted ones
                    props TEXT);
                CREATE TABLE edges(
                    src TEXT REFERENCES nodes(id),
                    dst TEXT REFERENCES nodes(id),
                    type TEXT, action_type TEXT, props TEXT);
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
                """;
            schema.ExecuteNonQuery();
        }

        // meta: self-identifying provenance so a loose .db says which database and
        // project it came from, when, and with which tool/format - written at
        // creation time. Query with: SELECT value FROM meta WHERE key='database';
        using (var mc = conn.CreateCommand())
        {
            mc.CommandText = "INSERT INTO meta VALUES ($k,$v)";
            var mk = AddParam(mc, "$k");
            var mv = AddParam(mc, "$v");
            void Meta(string k, string v) { mk.Value = k; mv.Value = v; mc.ExecuteNonQuery(); }
            Meta("database", database);
            Meta("project", project);
            Meta("generated_at", DateTime.Now.ToString("o"));
            Meta("tool", "sql-analyzer/tsql-parser");
            Meta("format", "graph-sqlite-v1");
            Meta("node_count", graph.Nodes.Count.ToString());
            Meta("edge_count", graph.Relationships.Count.ToString());
        }

        using var tx = conn.BeginTransaction();

        using (var nc = conn.CreateCommand())
        {
            nc.CommandText =
                """
                INSERT OR IGNORE INTO nodes
                  (id,label,name,db,cyclomatic_complexity,total_steps,dynamic_sql_steps,unresolved_dynamic_sql_steps,max_nesting,
                   object_type,schema_name,has_error_handling,has_cursor,has_transaction,
                   action,is_dynamic_sql,nesting_level,data_type,is_nullable,is_primary_key,props)
                VALUES ($id,$label,$name,$db,$cc,$ts,$ds,$uds,$mn,
                        $otype,$schema,$herr,$hcur,$htx,
                        $action,$dyn,$nest,$dtype,$nullable,$pk,$props)
                """;
            var pId = AddParam(nc, "$id");
            var pLabel = AddParam(nc, "$label");
            var pName = AddParam(nc, "$name");
            var pDb = AddParam(nc, "$db");
            var pCc = AddParam(nc, "$cc");
            var pTs = AddParam(nc, "$ts");
            var pDs = AddParam(nc, "$ds");
            var pUds = AddParam(nc, "$uds");
            var pMn = AddParam(nc, "$mn");
            var pOtype = AddParam(nc, "$otype");
            var pSchema = AddParam(nc, "$schema");
            var pHerr = AddParam(nc, "$herr");
            var pHcur = AddParam(nc, "$hcur");
            var pHtx = AddParam(nc, "$htx");
            var pAction = AddParam(nc, "$action");
            var pDyn = AddParam(nc, "$dyn");
            var pNest = AddParam(nc, "$nest");
            var pDtype = AddParam(nc, "$dtype");
            var pNullable = AddParam(nc, "$nullable");
            var pPk = AddParam(nc, "$pk");
            var pProps = AddParam(nc, "$props");

            foreach (var n in graph.Nodes)
            {
                var label = n.Labels.Count > 0 ? n.Labels[0] : "Node";
                var isObject = label == "SqlObject";
                pId.Value = n.Id;
                pLabel.Value = label;
                pName.Value = DisplayName(n);
                pDb.Value = Prop(n, "database");
                pCc.Value = isObject ? Prop(n, "cyclomatic_complexity") : DBNull.Value;
                pTs.Value = isObject ? totalSteps.GetValueOrDefault(n.Id) : DBNull.Value;
                pDs.Value = isObject ? dynamicSteps.GetValueOrDefault(n.Id) : DBNull.Value;
                pUds.Value = isObject ? unresolvedDynamicSteps.GetValueOrDefault(n.Id) : DBNull.Value;
                pMn.Value = isObject ? maxNesting.GetValueOrDefault(n.Id) : DBNull.Value;
                // Promoted columns: cheap, low-cardinality dimensions that audit/analysis
                // queries filter or group by (see scripts/lineage-queries.sql audit set).
                // Each only has a value on the label it belongs to; NULL elsewhere.
                pOtype.Value = Prop(n, "object_type");
                pSchema.Value = Prop(n, "schema");
                pHerr.Value = Prop(n, "has_error_handling");
                pHcur.Value = Prop(n, "has_cursor");
                pHtx.Value = Prop(n, "has_transaction");
                pAction.Value = Prop(n, "action");
                pDyn.Value = Prop(n, "is_dynamic_sql");
                pNest.Value = Prop(n, "nesting_level");
                pDtype.Value = Prop(n, "data_type");
                pNullable.Value = Prop(n, "is_nullable");
                pPk.Value = Prop(n, "is_primary_key");
                // Full property bag as JSON so the .db is lossless vs graph_full.json:
                // every node detail (condition_path, target_name, ...) stays queryable
                // via json_extract(props,...) - including the promoted keys above.
                // "labels" is added here (not stored in n.Properties): the `label` column
                // only ever kept Labels[0], so a node with two labels (e.g. SqlObject +
                // Process) silently lost the second one. Additive: existing stores/readers
                // that only look at `label` are unaffected.
                var propsWithLabels = new Dictionary<string, object>(n.Properties) { ["labels"] = n.Labels };
                pProps.Value = JsonSerializer.Serialize(propsWithLabels);
                nc.ExecuteNonQuery();
            }
        }

        // Every edge endpoint must be a real node for the edges->nodes foreign keys
        // to hold. The upstream graph can carry orphan edges (a relationship whose
        // endpoint node was dropped); the node store reports those rather than
        // failing, so mirror that here - skip them, keeping the .db referentially
        // consistent by construction instead of rejecting the whole export.
        var nodeIds = new HashSet<string>(graph.Nodes.Select(n => n.Id), StringComparer.Ordinal);

        using (var ec = conn.CreateCommand())
        {
            ec.CommandText = "INSERT INTO edges (src,dst,type,action_type,props) VALUES ($src,$dst,$type,$atype,$props)";
            var pSrc = AddParam(ec, "$src");
            var pDst = AddParam(ec, "$dst");
            var pType = AddParam(ec, "$type");
            var pAtype = AddParam(ec, "$atype");
            var pProps = AddParam(ec, "$props");

            foreach (var r in graph.Relationships)
            {
                if (!nodeIds.Contains(r.StartNodeId) || !nodeIds.Contains(r.EndNodeId))
                    continue;
                pSrc.Value = r.StartNodeId;
                pDst.Value = r.EndNodeId;
                pType.Value = r.Type;
                // action_type (INSERT/UPDATE/SELECT) promoted off WRITES_TO/READS_FROM
                // edges so write/read analysis filters without touching props JSON.
                pAtype.Value = r.Properties.TryGetValue("action_type", out var at) && at is string ats
                    ? ats : DBNull.Value;
                pProps.Value = r.Properties.Count > 0
                    ? JsonSerializer.Serialize(r.Properties)
                    : (object)DBNull.Value;
                ec.ExecuteNonQuery();
            }
        }

        tx.Commit();

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText =
                """
                CREATE INDEX ix_edges_src ON edges(src);
                CREATE INDEX ix_edges_dst ON edges(dst);
                CREATE INDEX ix_edges_type ON edges(type);
                CREATE INDEX ix_edges_action_type ON edges(action_type);
                CREATE INDEX ix_nodes_label ON nodes(label);
                CREATE INDEX ix_nodes_action ON nodes(action);
                CREATE INDEX ix_nodes_object_type ON nodes(object_type);
                CREATE INDEX ix_nodes_data_type ON nodes(data_type);
                """;
            idx.ExecuteNonQuery();
        }
    }

    private static SqliteParameter AddParam(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>A node property as a SQLite-bindable value: bool -&gt; 1/0, missing -&gt; NULL.</summary>
    private static object Prop(GraphNode n, string key) =>
        n.Properties.TryGetValue(key, out var v) && v is not null
            ? (v is bool b ? (b ? 1 : 0) : v)
            : DBNull.Value;

    private static string DisplayName(GraphNode n)
    {
        foreach (var key in new[] { "full_name", "name" })
            if (n.Properties.TryGetValue(key, out var v) && v is string s && s.Length > 0)
                return s;
        return n.Id;
    }
}
