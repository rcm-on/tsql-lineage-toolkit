using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public sealed class StoreInfoTool : IMcpTool
{
    public string Name => "store_info";

    public string Description =>
        "Snapshot of this graph store: is it usable, and is it fresh? Returns the meta " +
        "provenance (database, project, generated_at, format, node/edge counts), node counts " +
        "by label and edge counts by type (top 8 each, truncated:true if there are more), and " +
        "how many SqlObjects have dynamic SQL that never resolved (a standing blind spot for " +
        "impact/column_impact). Also returns dias_desde_generado and, past 30 days, an explicit " +
        "aviso: a stale store answers every other tool call normally while silently describing " +
        "a database that no longer exists. Call this first when starting a session against an " +
        "unfamiliar store, before resolve_object or impact.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>(),
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) => McpTools.StoreInfo(conn);
}

public sealed class DescribeObjectTool : IMcpTool
{
    public string Name => "describe_object";

    public string Description =>
        "Full profile of a SqlObject node id (get one from resolve_object first) - the thing " +
        "resolve_object can point you to but not read, and impact only walks edges from " +
        "without describing. Returns the node's scalars (object_type, schema_name, " +
        "cyclomatic_complexity, total_steps, dynamic_sql_steps, unresolved_dynamic_sql_steps, " +
        "max_nesting, has_error_handling, has_cursor, has_transaction - NULL ones are omitted, " +
        "never returned as null), the tables it reads/writes (tablas_leidas/tablas_escritas), " +
        "and what it calls or is called by (llama_a/llamado_por). For a Table or Column id it " +
        "throws instead of returning an empty profile, pointing to impact or column_impact.";

    public object InputSchema => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Canonical SqlObject node id, from resolve_object.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Max entries per list (tablas_leidas, tablas_escritas, llama_a, llamado_por); default 10.",
            },
        },
        ["required"] = new object[] { "id" },
    };

    public Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args) =>
        McpTools.DescribeObject(conn, McpArgs.String(args, "id") ?? "", McpArgs.Int(args, "limit") ?? 10);
}
