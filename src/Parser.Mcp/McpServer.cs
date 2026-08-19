using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

/// <summary>
/// Hand-rolled MCP transport: newline-delimited JSON-RPC 2.0 over stdin/stdout.
/// Methods: initialize, tools/list, tools/call (resolve_object, impact) - see
/// McpTools for the actual query logic. No official SDK dependency; see
/// notes/checkpoints for why.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions WireOptions = new() { WriteIndented = false };

    public static int Run(string storePath)
    {
        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine($"No existe la base SQLite '{storePath}'.");
            return 1;
        }

        var connStr = new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var response = HandleLine(conn, line);
            if (response == null)
                continue; // notification: no response per JSON-RPC 2.0
            Console.Out.Write(response);
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
        return 0;
    }

    /// <summary>One JSON-RPC request/notification in, one response line out (or null
    /// for a notification). Never throws - every failure path becomes a JSON-RPC
    /// error object so a malformed message can't kill the stdio loop.</summary>
    internal static string? HandleLine(SqliteConnection conn, string line)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return Envelope(null, error: JsonRpcError(-32700, $"Parse error: {ex.Message}"));
        }

        if (root is not JsonObject obj)
            return Envelope(null, error: JsonRpcError(-32600, "Invalid Request"));

        var idNode = obj["id"];
        var isNotification = idNode is null;

        try
        {
            var method = obj["method"]?.GetValue<string>();
            var @params = obj["params"] as JsonObject;
            object? result = method switch
            {
                "initialize" => Initialize(),
                "tools/list" => ToolsList(),
                "tools/call" => ToolsCall(conn, @params),
                _ => throw new McpMethodNotFoundException(method ?? "(null)"),
            };
            return isNotification ? null : Envelope(idNode, result: result);
        }
        catch (McpMethodNotFoundException ex)
        {
            return isNotification ? null : Envelope(idNode, error: JsonRpcError(-32601, $"Method not found: {ex.Message}"));
        }
        catch (McpInvalidParamsException ex)
        {
            return isNotification ? null : Envelope(idNode, error: JsonRpcError(-32602, ex.Message));
        }
        catch (Exception ex)
        {
            return isNotification ? null : Envelope(idNode, error: JsonRpcError(-32000, ex.Message));
        }
    }

    private static Dictionary<string, object?> Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
        ["serverInfo"] = new Dictionary<string, object?> { ["name"] = "tsql-lineage-mcp", ["version"] = "0.1.0" },
    };

    private static Dictionary<string, object?> ToolsList() => new()
    {
        ["tools"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "resolve_object",
                // Read by a model deciding whether to call this - concrete, with examples.
                ["description"] =
                    "Resolves a loose or ambiguous SQL object/table/column name (e.g. 'OrderLines', " +
                    "'sales.orderlines', 'usp_GetCustomer') into the canonical node ids this graph " +
                    "actually uses (e.g. 'MyDb:table:sales.orderlines'). Call this FIRST, before " +
                    "'impact' - impact needs an exact id and will not guess one from a plain name. " +
                    "Matches are ranked most-specific-first (exact > suffix > substring); when exactly " +
                    "one exact match exists the result carries exact:true.",
                ["inputSchema"] = new Dictionary<string, object?>
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
                },
            },
            new Dictionary<string, object?>
            {
                ["name"] = "impact",
                ["description"] =
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
                    "'hint' - never treat a bare empty result as 'nothing depends on this'.",
                ["inputSchema"] = new Dictionary<string, object?>
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
                },
            },
        },
    };

    private static Dictionary<string, object?> ToolsCall(SqliteConnection conn, JsonObject? @params)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(toolName))
            throw new McpInvalidParamsException("tools/call requiere 'name'.");
        var args = @params?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            Dictionary<string, object?>? data = toolName switch
            {
                "resolve_object" => McpTools.ResolveObject(conn, ArgString(args, "name") ?? "", ArgInt(args, "limit") ?? 10),
                "impact" => McpTools.Impact(conn, ArgString(args, "id") ?? "", ArgString(args, "direction") ?? "downstream", ArgInt(args, "depth") ?? 1, ArgInt(args, "limit") ?? 50),
                _ => null,
            };
            if (data == null)
                throw new McpInvalidParamsException($"Herramienta desconocida: '{toolName}'.");

            return ToolResult(JsonSerializer.Serialize(data, WireOptions), isError: false);
        }
        catch (McpToolException ex)
        {
            return ToolResult(ex.Message, isError: true);
        }
    }

    private static Dictionary<string, object?> ToolResult(string text, bool isError) => new()
    {
        ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    private static string? ArgString(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? ArgInt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string Envelope(JsonNode? id, object? result = null, object? error = null)
    {
        var obj = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error != null) obj["error"] = error; else obj["result"] = result;
        return JsonSerializer.Serialize(obj, WireOptions);
    }

    private static Dictionary<string, object?> JsonRpcError(int code, string message) => new()
    {
        ["code"] = code,
        ["message"] = message,
    };
}

internal sealed class McpMethodNotFoundException(string method) : Exception(method);
internal sealed class McpInvalidParamsException(string message) : Exception(message);
