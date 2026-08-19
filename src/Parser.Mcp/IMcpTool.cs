using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

/// <summary>
/// Una herramienta MCP. Antes, cada una vivía en dos sitios de McpServer (el array literal
/// de tools/list y el switch de tools/call): dos ediciones por herramienta y un fallo
/// latente si se olvidaba una. Aquí van juntas, y el registro es la única lista.
/// </summary>
public interface IMcpTool
{
    string Name { get; }

    /// <summary>La lee un modelo decidiendo si llamar. Concreta, con ejemplos.</summary>
    string Description { get; }

    /// <summary>JSON Schema de los argumentos, tal cual sale en tools/list.</summary>
    object InputSchema { get; }

    /// <summary>
    /// Consulta pura: conexión abierta en solo lectura + argumentos -&gt; respuesta.
    /// Lanza <see cref="McpToolException"/> ante argumentos inválidos; nunca escribe
    /// en stdout ni conoce el transporte.
    /// </summary>
    Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args);
}

/// <summary>Lectura tolerante de argumentos JSON-RPC: ausente o de otro tipo -&gt; null.</summary>
public static class McpArgs
{
    public static string? String(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? Int(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
