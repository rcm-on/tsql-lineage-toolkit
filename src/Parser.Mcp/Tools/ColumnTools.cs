using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class ColumnProvenanceTool : IMcpTool
{
    public string Name => "column_provenance";

    public string Description =>
        "Given a COLUMN node id (get one from resolve_object first), answers 'where does this " +
        "column's value come from': walks DERIVES_FROM forwards to the source columns it is " +
        "computed from (INSERT...SELECT, computed columns, view expressions), up to `depth` hops. " +
        "Results are ordered DEEPEST FIRST, which is the remediation order: fix the ultimate " +
        "source before the columns downstream of it. This is data-value lineage, a different " +
        "question from 'who references this column' - for that use column_impact. An empty " +
        "'sources' always carries a 'reason' (typically: it is a base column, nothing computes it) " +
        "and, when the opposite direction would answer, a 'hint'.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Canonical Column node id, e.g. 'MyDb:table:sales.orderlines:column:LineTotal'.",
            },
            ["depth"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Hops to traverse, 1-20 (default 5). Chains are usually short.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max source columns to return (default 20).",
            },
        },
        ["required"] = new object[] { "id" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        McpTools.ColumnProvenance(conn, McpArgs.String(args, "id") ?? "", McpArgs.Int(args, "depth") ?? 5, McpArgs.Int(args, "limit") ?? 20);
}

public sealed class ColumnImpactTool : IMcpTool
{
    public string Name => "column_impact";

    public string Description =>
        "Given a COLUMN node id (get one from resolve_object first), answers 'what breaks if I " +
        "change, rename or drop this column'. Returns TWO separate answers, deliberately not " +
        "merged: 'objects' = procs/views/functions that reference it, each tagged with confianza " +
        "'seguro' (referenced literally in the SQL) or 'probable' plus a motivo ('via vista' when " +
        "resolved through a view, 'de SELECT *' when it came from expanding a star); and 'columns' " +
        "= columns whose VALUE is computed from it, via DERIVES_FROM, with their hop distance. " +
        "It may also carry 'desconocido': objects in the same database whose dynamic SQL never " +
        "resolved to a literal, so they might touch this column and the engine has no edge to " +
        "prove or disprove it - that is a standing disclaimer, never 'no impact found'. For a " +
        "table or a procedure instead of a column, use impact.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Canonical Column node id, e.g. 'MyDb:table:application.people:column:FullName'.",
            },
            ["depth"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Hops for the derived-columns walk, 1-5 (default 3).",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max entries per list (default 15).",
            },
        },
        ["required"] = new object[] { "id" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        McpTools.ColumnImpact(conn, McpArgs.String(args, "id") ?? "", McpArgs.Int(args, "depth") ?? 3, McpArgs.Int(args, "limit") ?? 15);
}
