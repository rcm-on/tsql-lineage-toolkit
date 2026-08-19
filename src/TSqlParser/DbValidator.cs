using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// Cross-checks a previously exported graph (graph_full.json etc.) against the
/// live SQL Server: FK_TO relationships against sys.foreign_keys, and CALLS
/// (EXEC) relationships against sys.sql_expression_dependencies. Pure
/// read-only validation - no graph is regenerated here.
/// </summary>
public static class DbValidator
{
    public static int Run(string graphPath, string server)
    {
        var graphJson = File.ReadAllText(graphPath);
        using var doc = JsonDocument.Parse(graphJson);
        var root = doc.RootElement;

        // tableId -> ("schema.table" lowercase, no brackets), tableId -> database
        var tableDb = new Dictionary<string, string>();
        var tableName = new Dictionary<string, string>();
        // objectId -> database, for SqlObject nodes (used for CALLS validation)
        var objectDb = new Dictionary<string, string>();

        foreach (var node in Prop(root, "Nodes", "nodes").EnumerateArray())
        {
            var labels = Prop(node, "Labels", "labels").EnumerateArray().Select(l => l.GetString()).ToList();
            var id = Prop(node, "Id", "id").GetString()!;
            var props = Prop(node, "Properties", "properties");

            if (labels.Contains("Table"))
            {
                tableDb[id] = GetString(props, "database");
                var raw = GetString(props, "name");
                tableName[id] = raw.Replace("[", "").Replace("]", "").ToLowerInvariant();
            }
            else if (labels.Contains("SqlObject"))
            {
                objectDb[id] = GetString(props, "database");
            }
        }

        var graphFks = new HashSet<(string db, string child, string parent)>();
        var graphCalls = new HashSet<(string db, string caller, string callee)>();

        foreach (var rel in Prop(root, "Relationships", "relationships").EnumerateArray())
        {
            var type = Prop(rel, "Type", "type").GetString();
            var startId = Prop(rel, "StartNodeId", "source").GetString()!;
            var endId = Prop(rel, "EndNodeId", "target").GetString()!;

            if (type == "FK_TO" && tableDb.TryGetValue(startId, out var fkDb) && tableDb.ContainsKey(endId))
                graphFks.Add((fkDb, tableName[startId], tableName[endId]));
            else if (type == "CALLS" && objectDb.TryGetValue(startId, out var callDb) && objectDb.ContainsKey(endId))
                graphCalls.Add((callDb, PlainName(startId), PlainName(endId)));
        }

        Console.WriteLine($"FK_TO edges in graph: {graphFks.Count}");
        Console.WriteLine($"CALLS edges in graph: {graphCalls.Count}");

        var dbNames = tableDb.Values.Concat(objectDb.Values).Where(d => d.Length > 0).Distinct();
        var knownTables = new Dictionary<string, HashSet<string>>();
        foreach (var (id, db) in tableDb)
            (knownTables.TryGetValue(db, out var set) ? set : knownTables[db] = new HashSet<string>()).Add(tableName[id]);

        var dbFks = new HashSet<(string db, string child, string parent)>();
        var dbCalls = new HashSet<(string db, string caller, string callee)>();

        foreach (var db in dbNames)
        {
            Console.WriteLine($"\n=== {db} ===");
            using var conn = Connect(server, db);
            if (conn == null)
            {
                Console.WriteLine("  Could not connect.");
                continue;
            }

            foreach (var (child, parent) in QueryForeignKeys(conn))
                dbFks.Add((db, child, parent));

            foreach (var (caller, callee) in QueryProcedureCalls(conn))
                dbCalls.Add((db, caller, callee));
        }

        var dbFksInScope = dbFks.Where(f => knownTables.TryGetValue(f.db, out var t) && t.Contains(f.child) && t.Contains(f.parent)).ToHashSet();
        Console.WriteLine($"\nFK relationships in DB restricted to tables present in graph: {dbFksInScope.Count}");

        var missingFks = dbFksInScope.Except(graphFks).ToList();
        var extraFks = graphFks.Except(dbFksInScope).ToList();
        Console.WriteLine($"  In DB but missing from graph: {missingFks.Count}");
        foreach (var fk in missingFks) Console.WriteLine($"    {fk}");
        Console.WriteLine($"  In graph but not in DB (within scope): {extraFks.Count}");
        foreach (var fk in extraFks) Console.WriteLine($"    {fk}");

        var knownObjects = objectDb.Keys.Select(PlainName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbCallsInScope = dbCalls.Where(c => knownObjects.Contains(c.caller) && knownObjects.Contains(c.callee)).ToHashSet();
        Console.WriteLine($"\nCALLS (EXEC) relationships in DB restricted to analyzed objects: {dbCallsInScope.Count}");

        var missingCalls = dbCallsInScope.Except(graphCalls).ToList();
        var extraCalls = graphCalls.Except(dbCallsInScope).ToList();
        Console.WriteLine($"  In DB but missing from graph: {missingCalls.Count}");
        foreach (var c in missingCalls) Console.WriteLine($"    {c}");
        // Symmetric side: without this, a phantom CALLS edge invented by the parser
        // passes validation unnoticed (the FK check has always looked both ways).
        Console.WriteLine($"  In graph but not in DB (within scope): {extraCalls.Count}");
        foreach (var c in extraCalls) Console.WriteLine($"    {c}");

        // Dependencies whose CALLER is a table (computed columns, CHECK/DEFAULT
        // constraints invoking a UDF). They can never appear in graphCalls because
        // tables are :Table nodes, not :SqlObject - report them so the gap stays
        // visible instead of being silently filtered out by knownObjects. Views are
        // materialised as BOTH :SqlObject and :Table, so pairs the graph already has
        // a CALLS edge for are excluded, or this would cry wolf on every view->UDF.
        var fromTables = dbCalls
            .Where(c => knownTables.TryGetValue(c.db, out var t) && t.Contains(c.caller)
                        && knownObjects.Contains(c.callee)
                        && !graphCalls.Contains(c))
            .ToList();
        Console.WriteLine($"\nDependencies from TABLES to functions (computed columns / constraints): {fromTables.Count}");
        foreach (var c in fromTables) Console.WriteLine($"    {c}  [not modelled]");

        return 0;
    }

    /// <summary>"Database::Schema.Object" -> "schema.object" (lowercase, for set comparisons).</summary>
    private static string PlainName(string objectId)
    {
        var idx = objectId.IndexOf("::", StringComparison.Ordinal);
        return (idx >= 0 ? objectId[(idx + 2)..] : objectId).Replace("[", "").Replace("]", "").ToLowerInvariant();
    }

    /// <summary>
    /// Reads a property by either its PascalCase (historic Neo4j shape) or camelCase
    /// (current parser output) name - the graph JSON is camelCase (nodes/id/labels/
    /// properties, source/target) while this validator was written against the old
    /// PascalCase keys. Mirrors the dual-format handling in dashboard/src/shape.js.
    /// </summary>
    private static JsonElement Prop(JsonElement el, string pascal, string camel) =>
        el.TryGetProperty(pascal, out var v) ? v : el.GetProperty(camel);

    private static string GetString(JsonElement props, string name) =>
        props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static SqlConnection? Connect(string server, string database)
    {
        var connStr = SqlConnections.Build(server, database, 10, SqlConnections.FromEnvironment());
        try
        {
            var conn = new SqlConnection(connStr);
            conn.Open();
            return conn;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static IEnumerable<(string child, string parent)> QueryForeignKeys(SqlConnection conn)
    {
        const string sql = """
            SELECT
                LOWER(SCHEMA_NAME(o.schema_id) + '.' + o.name)   AS child_table,
                LOWER(SCHEMA_NAME(rt.schema_id) + '.' + rt.name) AS parent_table
            FROM sys.foreign_keys fk
            JOIN sys.objects o  ON o.object_id = fk.parent_object_id
            JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
        """;
        using var cmd = new SqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Mirrors src/db_extractor.py's get_procedure_call_graph: EXEC dependencies between procs/functions.</summary>
    private static IEnumerable<(string caller, string callee)> QueryProcedureCalls(SqlConnection conn)
    {
        const string sql = """
            SELECT DISTINCT
                LOWER(SCHEMA_NAME(ref.schema_id) + '.' + ref.name) AS caller,
                LOWER(SCHEMA_NAME(tgt.schema_id) + '.' + tgt.name) AS callee
            FROM sys.sql_expression_dependencies dep
            JOIN sys.objects ref ON dep.referencing_id = ref.object_id
            JOIN sys.objects tgt ON dep.referenced_id   = tgt.object_id
            WHERE ref.type IN ('P', 'TR', 'FN', 'IF', 'TF', 'V', 'U')
              AND tgt.type IN ('P', 'FN', 'IF', 'TF')
              AND ref.is_ms_shipped = 0
              AND tgt.is_ms_shipped = 0
        """;
        using var cmd = new SqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetString(1));
    }
}
