using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class RiskTool : IMcpTool
{
    public string Name => "risks";

    public string Description =>
        "Bad-practice / risk findings over this graph (SQL injection, cursors without error " +
        "handling, UPDATE/DELETE without WHERE, tables without a primary key, etc.) - the same " +
        "rule engine the dashboard runs, applied to this store. Severities (crit/high/med/low/info) " +
        "are an engine convention, not a measurement: they rank how bad a pattern looks in isolation, " +
        "not how often the code runs. Every finding is structural (evidencia='estructural') because " +
        "every current rule reads static shape, not execution - the response's datos_de_ejecucion " +
        "flag says whether this store was ever enriched with execution plans, and carries a warning " +
        "when it wasn't: without execution data, a rarely-run object and a hot-path object get the " +
        "same verdict. severity filters that level and everything more severe (e.g. severity='high' " +
        "returns crit+high). Zero findings does not mean zero risk - only that no current rule fired.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["severity"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Minimum severity to include, that level and worse: crit/high/med/low/info. Default: all.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max findings to return; default 15, capped lower to fit the response budget (findings carry free-text detail).",
            },
        },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        RiskQueries.Risks(conn, McpArgs.String(args, "severity"), McpArgs.Int(args, "limit") ?? 15);
}
