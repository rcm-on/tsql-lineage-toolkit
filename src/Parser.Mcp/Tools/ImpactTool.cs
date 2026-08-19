using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class ImpactTool : IMcpTool
{
    public string Name => "impact";

    public string Description =>
        "Given a canonical node id (get one from resolve_object first), walks the call/data " +
        "lineage edges (CALLS, READS_FROM, WRITES_TO, DERIVES_FROM, READS_COLUMN, " +
        "WRITES_COLUMN) up to `depth` hops and returns who is affected, grouped by hop " +
        "distance. direction='downstream' answers 'what breaks if I change this' (its " +
        "callers/consumers, the default); direction='upstream' answers 'what does this " +
        "depend on' (its callees/sources). For a TABLE specifically: downstream = which " +
        "procs/views read or write it; upstream = where its data comes from (usually empty - " +
        "tables rarely have outgoing lineage edges of their own). Example: impact with id " +
        "'MyDb::dbo.usp_UpdateOrders', direction 'downstream', depth 2 finds procs that " +
        "would break two calls away if usp_UpdateOrders changed. An empty 'affected' always " +
        "carries a 'reason' (why it's empty) and, when the other direction would answer, a " +
        "'hint' - never treat a bare empty result as 'nothing depends on this'.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Canonical node id, from resolve_object.",
            },
            ["direction"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = new object[] { "downstream", "upstream" },
                ["description"] = "downstream = what breaks if this changes (default); upstream = what this depends on.",
            },
            ["depth"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Hops to traverse, 1-5 (default 1).",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max affected nodes to return (default 50).",
            },
        },
        ["required"] = new object[] { "id" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        McpTools.Impact(
            conn,
            McpArgs.String(args, "id") ?? "",
            McpArgs.String(args, "direction") ?? "downstream",
            McpArgs.Int(args, "depth") ?? 1,
            McpArgs.Int(args, "limit") ?? 50);
}
