using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class BlindSpotsTool : IMcpTool
{
    public string Name => "blind_spots";

    public string Description =>
        "Objects in this graph with unresolved dynamic SQL: the outer perimeter of what " +
        "this extraction cannot assert. When a SqlObject has unresolved_dynamic_sql_steps > 0, " +
        "it might be reading or writing any table—and there is no edge that proves or disproves it. " +
        "This is not an error list; it is the surface of the blind spot. Returns the object ids/names " +
        "with step counts, the total count, and the sum of all dynamic_sql_steps across all SqlObjects " +
        "for context. If there is no unresolved dynamic SQL, explains why that does not mean the " +
        "extractor sees everything.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max objects to list; default 20.",
            },
        },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        BlindSpotsQueries.BlindSpots(conn, McpArgs.Int(args, "limit") ?? 20);
}
