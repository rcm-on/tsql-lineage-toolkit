using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class ResolveObjectTool : IMcpTool
{
    public string Name => "resolve_object";

    public string Description =>
        "Resolves a loose or ambiguous SQL object/table/column name (e.g. 'OrderLines', " +
        "'sales.orderlines', 'usp_GetCustomer') into the canonical node ids this graph " +
        "actually uses (e.g. 'MyDb:table:sales.orderlines'). Call this FIRST, before " +
        "'impact' - impact needs an exact id and will not guess one from a plain name. " +
        "Matches are ranked most-specific-first (exact > suffix > substring); when exactly " +
        "one exact match exists the result carries exact:true.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["name"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Loose or partial object/table/column name to resolve, e.g. 'OrderLines'.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max matches to return (default 10).",
            },
        },
        ["required"] = new object[] { "name" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        McpTools.ResolveObject(conn, McpArgs.String(args, "name") ?? "", McpArgs.Int(args, "limit") ?? 10);
}
