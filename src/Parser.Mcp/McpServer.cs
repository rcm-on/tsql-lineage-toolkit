using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

/// <summary>
/// Transporte MCP a mano: JSON-RPC 2.0 delimitado por saltos de línea sobre stdin/stdout.
/// Métodos: initialize, tools/list, tools/call, prompts/list, prompts/get. Las herramientas vienen del registro
/// inyectado, así que el transporte no sabe cuáles hay. Sin SDK oficial; el porqué está en
/// notes/checkpoints/T16.md.
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions WireOptions = new() { WriteIndented = false };

    private readonly IReadOnlyList<IMcpTool> _tools;

    /// <param name="tools">Registro a servir. Por defecto <see cref="McpToolRegistry.Default"/>;
    /// los tests inyectan el suyo sin tocar el registro real.</param>
    public McpServer(IReadOnlyList<IMcpTool>? tools = null) => _tools = tools ?? McpToolRegistry.Default;

    public int Run(string storePath)
    {
        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine($"No existe la base SQLite '{storePath}'.");
            Console.Error.WriteLine("Genérala con:  TSqlParser quickstart <base> [--server <servidor>]");
            Console.Error.WriteLine("o, desde un input.json ya extraído:  TSqlParser <input.json> graph_full.json --columns --sqlite");
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
                continue; // notificación: sin respuesta, según JSON-RPC 2.0
            Console.Out.Write(response);
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
        return 0;
    }

    /// <summary>Una petición/notificación entra, una línea de respuesta sale (o null si es
    /// notificación). Nunca lanza: cualquier fallo se convierte en un error JSON-RPC, para
    /// que un mensaje malformado no mate el bucle de stdio.</summary>
    internal string? HandleLine(SqliteConnection conn, string line)
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
                "prompts/list" => PromptsList(),
                "prompts/get" => PromptsGet(@params),
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
        ["capabilities"] = new Dictionary<string, object?>
        {
            ["tools"] = new Dictionary<string, object?>(),
            ["prompts"] = new Dictionary<string, object?>(),
        },
        ["serverInfo"] = new Dictionary<string, object?> { ["name"] = "tsql-lineage-mcp", ["version"] = "0.1.0" },
    };

    private Dictionary<string, object?> ToolsList() => new()
    {
        ["tools"] = _tools.Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema,
        }).ToList(),
    };

    private static Dictionary<string, object?> PromptsList() => new()
    {
        ["prompts"] = McpPrompts.Default.Select(p => new Dictionary<string, object?>
        {
            ["name"] = p.Name,
            ["description"] = p.Description,
            ["arguments"] = p.Arguments.Select(a => new Dictionary<string, object?>
            {
                ["name"] = a.Name,
                ["description"] = a.Description,
                ["required"] = a.Required,
            }).ToList(),
        }).ToList(),
    };

    private static Dictionary<string, object?> PromptsGet(JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
            throw new McpInvalidParamsException("prompts/get requiere 'name'.");

        var prompt = McpPrompts.Default.FirstOrDefault(p => p.Name == name)
                     ?? throw new McpInvalidParamsException($"Prompt desconocido: '{name}'.");

        var given = @params?["arguments"] as JsonObject;
        var values = new Dictionary<string, string>();
        foreach (var arg in prompt.Arguments)
        {
            var v = given is null ? null : McpArgs.String(given, arg.Name);
            if (string.IsNullOrWhiteSpace(v))
            {
                if (arg.Required)
                    throw new McpInvalidParamsException($"El prompt '{name}' requiere el argumento '{arg.Name}'.");
                continue;
            }
            values[arg.Name] = v;
        }

        return new()
        {
            ["description"] = prompt.Description,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt.Render(values) },
                },
            },
        };
    }

    private Dictionary<string, object?> ToolsCall(SqliteConnection conn, JsonObject? @params)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(toolName))
            throw new McpInvalidParamsException("tools/call requiere 'name'.");

        var tool = _tools.FirstOrDefault(t => t.Name == toolName)
                   ?? throw new McpInvalidParamsException($"Herramienta desconocida: '{toolName}'.");

        var args = @params?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            return ToolResult(JsonSerializer.Serialize(tool.Handle(conn, args), WireOptions), isError: false);
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
