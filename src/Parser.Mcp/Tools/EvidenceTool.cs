using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class EvidenceTool : IMcpTool
{
    public string Name => "evidence";

    public string Description =>
        "Why does this object touch that table or column? Returns the individual steps of a " +
        "SqlObject that produced the edge: line_no in the object's source, the step's action " +
        "(SELECT/INSERT/UPDATE/EXEC...), its label, the relation (READS_FROM/WRITES_TO/" +
        "READS_COLUMN/WRITES_COLUMN/FILTERS_ON), the resolution bucket (direct/star_expanded/" +
        "via_view) and, when the step is guarded, the IF it hangs from. Use it to check a claim " +
        "from impact, column_impact or risks instead of repeating it. Scope, stated up front: " +
        "line and step, NOT the literal SQL text - the store keeps position, not source. " +
        "'target' takes a node id or a loose name; ambiguous names come back as candidates.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["object_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Canonical SqlObject node id, from resolve_object.",
            },
            ["target"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Table or Column node id, or a loose name matched among what this object touches.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max steps returned; default 6.",
            },
        },
        ["required"] = new object[] { "object_id", "target" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        EvidenceQueries.Evidence(
            conn,
            McpArgs.String(args, "object_id") ?? "",
            McpArgs.String(args, "target") ?? "",
            McpArgs.Int(args, "limit") ?? 6);
}
