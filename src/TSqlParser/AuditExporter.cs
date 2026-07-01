using System.Text.Json;

namespace TSqlParser;

/// <summary>
/// Generates audit_report.json alongside index.json in the node store root.
/// Always rebuilt in full on every Build() call: it aggregates across the whole
/// graph, so content-hashing individual objects is wrong — one upstream proc
/// change can shift every risk_pattern entry.
///
/// Called by NodeStoreExporter.Build() as Capa 5, after lineage_path.json
/// (Capa 4) so lineageCache is fully populated before it is used here.
///
/// Important: in GraphPayload, WRITES_TO / READS_FROM edges originate from
/// Step nodes (ids like "DbId::Schema.Proc#step_N"), not from SqlObject nodes.
/// This mirrors what NodeStoreExporter.Build does in the model.json section:
/// roll up those edges to the owning SqlObject before counting degree or
/// building the writes/reads lists.
/// </summary>
internal static class AuditExporter
{
    public static string Generate(
        GraphPayload graph,
        Dictionary<string, (List<string> Roots, int Depth)> lineageCache,
        JsonSerializerOptions jsonOptions)
    {
        var nodeById = graph.Nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);

        var objectIds = new HashSet<string>(
            graph.Nodes.Where(n => n.Labels.Contains("SqlObject")).Select(n => n.Id),
            StringComparer.Ordinal);

        var tableIds = new HashSet<string>(
            graph.Nodes.Where(n => n.Labels.Contains("Table")).Select(n => n.Id),
            StringComparer.Ordinal);

        // Roll-up helper: for a Step id "ObjId#step_N" return the owning ObjId;
        // for a SqlObject id return itself; otherwise null.
        string? ObjectOwner(string id)
        {
            if (objectIds.Contains(id)) return id;
            var h = id.IndexOf('#');
            if (h > 0)
            {
                var prefix = id[..h];
                if (objectIds.Contains(prefix)) return prefix;
            }
            return null;
        }

        // ── Single pass: build all per-object aggregates ──────────────────
        // degree: rolled-up connectivity (CALLS/AFFECTS/FK_TO direct;
        // WRITES_TO/READS_FROM rolled up from Step to owning SqlObject, same as
        // the model.json macro-edge logic in NodeStoreExporter.Build).
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        void BumpDeg(string id) => degree[id] = degree.GetValueOrDefault(id) + 1;

        var writesToMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var readsFromMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var outputColsMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var connectedTableIds = new HashSet<string>(StringComparer.Ordinal);

        var macroSeen = new HashSet<(string Type, string From, string To)>();

        foreach (var rel in graph.Relationships)
        {
            if (!nodeById.ContainsKey(rel.StartNodeId) || !nodeById.ContainsKey(rel.EndNodeId))
                continue;

            switch (rel.Type)
            {
                case "CALLS" or "AFFECTS":
                {
                    // Direct SqlObject → SqlObject: no rollup needed.
                    if (macroSeen.Add((rel.Type, rel.StartNodeId, rel.EndNodeId)))
                    {
                        BumpDeg(rel.StartNodeId);
                        BumpDeg(rel.EndNodeId);
                    }
                    break;
                }

                case "FK_TO" or "REFERENCES":
                    connectedTableIds.Add(rel.StartNodeId);
                    connectedTableIds.Add(rel.EndNodeId);
                    break;

                case "WRITES_TO":
                {
                    if (!tableIds.Contains(rel.EndNodeId)) break;
                    var owner = ObjectOwner(rel.StartNodeId);
                    if (owner == null) break;
                    connectedTableIds.Add(rel.EndNodeId);
                    if (macroSeen.Add(("WRITES_TO", owner, rel.EndNodeId)))
                    {
                        BumpDeg(owner);
                        BumpDeg(rel.EndNodeId);
                        if (nodeById.TryGetValue(rel.EndNodeId, out var tbl))
                            ListAppend(writesToMap, owner, FullName(tbl));
                    }
                    break;
                }

                case "READS_FROM":
                {
                    if (!tableIds.Contains(rel.EndNodeId)) break;
                    var owner = ObjectOwner(rel.StartNodeId);
                    if (owner == null) break;
                    connectedTableIds.Add(rel.EndNodeId);
                    if (macroSeen.Add(("READS_FROM", owner, rel.EndNodeId)))
                    {
                        BumpDeg(owner);
                        BumpDeg(rel.EndNodeId);
                        if (nodeById.TryGetValue(rel.EndNodeId, out var tbl))
                            ListAppend(readsFromMap, owner, FullName(tbl));
                    }
                    break;
                }

                case "HAS_COLUMN":
                    if (objectIds.Contains(rel.StartNodeId))
                        ListAppend(outputColsMap, rel.StartNodeId, rel.EndNodeId);
                    break;
            }
        }

        static void ListAppend(Dictionary<string, List<string>> dict, string key, string value)
        {
            if (!dict.TryGetValue(key, out var list))
                dict[key] = list = new();
            list.Add(value);
        }

        // ── Unresolved dynamic SQL steps per SqlObject ────────────────────
        // A step is "unresolved" when is_dynamic_sql=true but dynamic_sql == ""
        // (parser failed closed; WRITES_TO/READS_FROM are an undercount).

        var unresolvedMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!n.Labels.Contains("Step")) continue;
            var owner = ObjectOwner(n.Id);
            if (owner == null) continue;
            if (n.Properties.TryGetValue("is_dynamic_sql", out var dyn) && dyn is true
                && (!n.Properties.TryGetValue("dynamic_sql", out var ds) || ds is not string { Length: > 0 }))
            {
                unresolvedMap[owner] = unresolvedMap.GetValueOrDefault(owner) + 1;
            }
        }

        // ── Invert lineageCache: root table key → set of view object IDs ──
        // lineageCache keys and root values are column node IDs.  The "table"
        // property of a Column node stores the schema-qualified table name used
        // as the key (e.g. "dbo.orders") — OrdinalIgnoreCase because the same
        // table can appear as "dbo.Orders" in WRITES_TO and "dbo.orders" in roots.

        var tableKeyToViews = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (objId, colIds) in outputColsMap)
        {
            foreach (var colId in colIds)
            {
                if (!lineageCache.TryGetValue(colId, out var lc)) continue;
                foreach (var rootId in lc.Roots)
                {
                    if (!nodeById.TryGetValue(rootId, out var rootCol)) continue;
                    if (!rootCol.Properties.TryGetValue("table", out var tProp)
                        || tProp is not string tbl || tbl.Length == 0) continue;
                    if (!tableKeyToViews.TryGetValue(tbl, out var vset))
                        tableKeyToViews[tbl] = vset = new(StringComparer.Ordinal);
                    vset.Add(objId);
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        string FullName(GraphNode n)
        {
            foreach (var k in new[] { "full_name", "name" })
                if (n.Properties.TryGetValue(k, out var v) && v is string s && s.Length > 0)
                    return s;
            return n.Id;
        }

        int GetInt(GraphNode n, string key)
        {
            if (!n.Properties.TryGetValue(key, out var v)) return 0;
            return v switch
            {
                int i => i,
                long l => (int)l,
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                _ => 0,
            };
        }

        // object_type property (StoredProcedure / View / Function / …).
        string ObjType(GraphNode n) =>
            n.Properties.TryGetValue("object_type", out var t) && t is string ts && ts.Length > 0 ? ts : "";

        // Returns sorted display names of views/TVFs/procs that ultimately trace
        // to any of the supplied table names via lineage_path.json roots.
        List<string> ViewsDependentOn(IEnumerable<string> tableNames)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tbl in tableNames)
                if (tableKeyToViews.TryGetValue(tbl, out var vs))
                    foreach (var v in vs) result.Add(v);
            return result
                .Select(id => nodeById.TryGetValue(id, out var n) ? FullName(n) : id)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }

        // ── 1. summary ───────────────────────────────────────────────────

        var byType = graph.Nodes
            .Where(n => n.Labels.Contains("SqlObject"))
            .GroupBy(n => ObjType(n), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.Length > 0 ? g.Key : "Unknown", g => g.Count(), StringComparer.Ordinal);

        var summary = new Dictionary<string, object>
        {
            ["objects"] = objectIds.Count,
            ["tables"] = tableIds.Count,
            ["columns"] = graph.Nodes.Count(n => n.Labels.Contains("Column")),
            ["business_rules"] = graph.Nodes.Count(n => n.Labels.Contains("BusinessRule")),
            ["schemas"] = graph.Nodes.Count(n => n.Labels.Contains("Schema")),
            ["databases"] = graph.Nodes.Count(n => n.Labels.Contains("Database")),
            ["parse_errors"] = graph.Nodes.Count(n =>
                n.Properties.TryGetValue("parse_error", out var e) && e is string es && es.Length > 0),
            ["by_type"] = byType,
        };

        // ── 2. hotspots (top-10 by cc × degree) ──────────────────────────
        // Score = cyclomatic_complexity × connectivity_degree; both must be > 0.
        // unresolved_dynamic_sql_steps is informational only — it must NOT be a
        // factor, or fully-resolved procs (unresolved=0) would score 0 and vanish.

        var hotspotItems = new List<(int score, int unres, string id, Dictionary<string, object> entry)>();
        foreach (var id in objectIds)
        {
            var n = nodeById[id];
            var cc = GetInt(n, "cyclomatic_complexity");
            var deg = degree.GetValueOrDefault(id);
            var score = cc * deg;
            if (score == 0) continue;
            var unres = unresolvedMap.GetValueOrDefault(id);
            var writes = (writesToMap.GetValueOrDefault(id) ?? new()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var reads = (readsFromMap.GetValueOrDefault(id) ?? new()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var views = ViewsDependentOn(writes);
            hotspotItems.Add((score, unres, id, new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = FullName(n),
                ["type"] = ObjType(n),
                ["score"] = score,
                ["cyclomatic_complexity"] = cc,
                ["degree"] = deg,
                ["unresolved_dynamic_sql_steps"] = unres,
                ["writes_tables"] = writes,
                ["reads_tables"] = reads,
                ["views_dependent_count"] = views.Count,
                ["views_dependent"] = views,
            }));
        }

        var hotspots = hotspotItems
            .OrderByDescending(h => h.score)
            .ThenByDescending(h => h.unres)
            .Take(10)
            .Select(h => (object)h.entry)
            .ToList();

        // ── 3. blind_spots ───────────────────────────────────────────────
        // severity_score = views_dependent_count × unresolved_steps: objects
        // that are both opaque and upstream of many client-facing views are
        // the most dangerous.

        var blindItems = new List<(int score, int unres, object entry)>();
        foreach (var id in objectIds)
        {
            var unres = unresolvedMap.GetValueOrDefault(id);
            if (unres == 0) continue;
            var n = nodeById[id];
            var writes = (writesToMap.GetValueOrDefault(id) ?? new()).Distinct().ToList();
            var views = ViewsDependentOn(writes);
            var score = views.Count * unres;
            blindItems.Add((score, unres, (object)new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = FullName(n),
                ["type"] = ObjType(n),
                ["unresolved_dynamic_sql_steps"] = unres,
                ["views_dependent_count"] = views.Count,
                ["views_dependent"] = views,
                ["severity_score"] = score,
            }));
        }

        var blindSpots = blindItems
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.unres)
            .Select(x => x.entry)
            .ToList();

        // ── 4. orphan_tables ─────────────────────────────────────────────
        // Tables with no WRITES_TO, READS_FROM, FK_TO, or REFERENCES edge.
        // HAS_COLUMN is excluded: every table has those, so raw degree is never 0.

        var orphanTables = graph.Nodes
            .Where(n => n.Labels.Contains("Table") && !connectedTableIds.Contains(n.Id))
            .OrderBy(n => FullName(n), StringComparer.Ordinal)
            .Select(n => (object)new Dictionary<string, object> { ["id"] = n.Id, ["name"] = FullName(n) })
            .ToList();

        // ── 5. lineage_coverage ──────────────────────────────────────────
        // % of output columns (from views/TVFs/procs with HAS_COLUMN from the
        // SqlObject) whose DERIVES_FROM chain traces to at least one base-table
        // column (depth > 0).  depth == 0 means the column is a root (no source).

        var allOutputColIds = outputColsMap.Values.SelectMany(l => l).ToHashSet(StringComparer.Ordinal);
        int colsTotal = allOutputColIds.Count;
        int colsWithLineage = allOutputColIds.Count(c => lineageCache.TryGetValue(c, out var lc) && lc.Depth > 0);

        var lineageCoverage = new Dictionary<string, object>
        {
            ["objects_with_output_columns"] = outputColsMap.Count,
            ["columns_total"] = colsTotal,
            ["columns_with_lineage"] = colsWithLineage,
            ["coverage_pct"] = colsTotal > 0 ? Math.Round(colsWithLineage * 100.0 / colsTotal, 1) : 100.0,
        };

        // ── 6. risk_patterns ─────────────────────────────────────────────
        // Detects: "object with opaque dynamic SQL writes to a table that is a
        // root in at least one view's lineage_path.json".  This is exactly the
        // pattern found independently by both agents in the audit exercise.

        var riskItems = new List<(int viewCount, string objId, string tbl, object entry)>();
        foreach (var objId in objectIds)
        {
            var unres = unresolvedMap.GetValueOrDefault(objId);
            if (unres == 0) continue;
            var n = nodeById[objId];
            var writes = (writesToMap.GetValueOrDefault(objId) ?? new()).Distinct();
            foreach (var tbl in writes)
            {
                var views = ViewsDependentOn(new[] { tbl });
                if (views.Count == 0) continue;
                var sev = views.Count >= 10 ? "critical" : views.Count >= 3 ? "high" : "medium";
                riskItems.Add((views.Count, objId, tbl, (object)new Dictionary<string, object>
                {
                    ["pattern"] = "opaque_write_to_client_facing_table",
                    ["severity"] = sev,
                    ["object_id"] = objId,
                    ["object_name"] = FullName(n),
                    ["table"] = tbl,
                    ["unresolved_dynamic_sql_steps"] = unres,
                    ["views_at_risk_count"] = views.Count,
                    ["views_at_risk"] = views,
                }));
            }
        }

        var riskPatterns = riskItems
            .OrderByDescending(r => r.viewCount)
            .ThenBy(r => r.objId, StringComparer.Ordinal)
            .ThenBy(r => r.tbl, StringComparer.Ordinal)
            .Select(r => r.entry)
            .ToList();

        // ── assemble ─────────────────────────────────────────────────────

        var report = new Dictionary<string, object>
        {
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["summary"] = summary,
            ["hotspots"] = hotspots,
            ["blind_spots"] = blindSpots,
            ["orphan_tables"] = orphanTables,
            ["lineage_coverage"] = lineageCoverage,
            ["risk_patterns"] = riskPatterns,
        };

        return JsonSerializer.Serialize(report, jsonOptions);
    }
}
