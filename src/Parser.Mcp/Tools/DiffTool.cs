using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class DiffImpactTool : IMcpTool
{
    public string Name => "diff_impact";

    public string Description =>
        "Given two node-store directories ('before' and 'after', each a '.nodes' store with " +
        "manifest.json and change_map.json, produced by --nodestore or update-nodestore), answers " +
        "the PR reviewer's question: 'does this change reach something it didn't reach before'. " +
        "Diffs objects_changed/added/removed by content_hash, and - the part that matters - " +
        "new_impact: via_calls/via_data edges and newly-reached objects that did not exist in " +
        "'before'. Also reports workflow entry points added/removed and how many workflows were " +
        "reshaped. An empty new_impact always carries a 'reason' (identical stores, or changes " +
        "that simply don't propagate) - never a bare empty result, which would read as 'no risk'.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["before"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Path to the 'before' .nodes store directory (contains manifest.json + change_map.json).",
            },
            ["after"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Path to the 'after' .nodes store directory, same shape as 'before'.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max entries per list (default 20). new_impact entries and their sub-lists are " +
                    "paged by this too but never past 8 entries / 5 per sub-list - they carry more per item than " +
                    "the other lists, so a larger page would blow the response budget.",
            },
        },
        ["required"] = new object[] { "before", "after" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        DiffQueries.Diff(McpArgs.String(args, "before") ?? "", McpArgs.String(args, "after") ?? "", McpArgs.Int(args, "limit") ?? 20);
}
