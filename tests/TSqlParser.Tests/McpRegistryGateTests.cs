using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

/// <summary>
/// Gates del registro de herramientas. Cubren herramientas que aún no existen: cualquiera
/// que se añada a McpToolRegistry.Default entra automáticamente en los tres.
/// </summary>
public class McpRegistryGateTests
{
    public static TheoryData<string> Herramientas()
    {
        var data = new TheoryData<string>();
        foreach (var t in McpToolRegistry.Default) data.Add(t.Name);
        return data;
    }

    [Fact]
    public void Registro_NoTieneNombresDuplicados()
    {
        var duplicados = McpToolRegistry.Default
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicados.Count == 0, $"Nombres repetidos en el registro: {string.Join(", ", duplicados)}.");
    }

    [Theory]
    [MemberData(nameof(Herramientas))]
    public void CadaHerramienta_DeclaraNombreDescripcionYEsquema(string nombre)
    {
        var tool = McpToolRegistry.Default.Single(t => t.Name == nombre);

        Assert.False(string.IsNullOrWhiteSpace(tool.Name));
        // La descripción la lee un modelo para decidir si llamar: una línea no basta.
        Assert.True(tool.Description.Length >= 80, $"'{nombre}' tiene una descripción de {tool.Description.Length} caracteres.");
        var schema = JsonSerializer.Serialize(tool.InputSchema);
        Assert.Contains("\"type\":\"object\"", schema);
        Assert.Contains("\"properties\"", schema);
    }

    [Theory]
    [MemberData(nameof(Herramientas))]
    public void CadaHerramientaDelRegistro_EsAlcanzableDesdeToolsCall(string nombre)
    {
        // El fallo que este gate impide: antes, tools/list salía de un array literal y
        // tools/call de un switch. Añadir a uno y olvidar el otro dejaba una herramienta
        // anunciada e inalcanzable, o alcanzable e invisible.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE nodes(id TEXT, label TEXT, name TEXT); CREATE TABLE edges(src TEXT, dst TEXT, type TEXT);";
            cmd.ExecuteNonQuery();
        }

        var server = new McpServer();
        var peticion = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\","
                     + "\"params\":{\"name\":\"" + nombre + "\",\"arguments\":{}}}";

        var respuesta = server.HandleLine(conn, peticion);

        Assert.NotNull(respuesta);
        using var doc = JsonDocument.Parse(respuesta!);
        // Argumentos vacíos pueden dar isError:true, y está bien. Lo que no puede salir es
        // un error de protocolo "herramienta desconocida".
        Assert.False(doc.RootElement.TryGetProperty("error", out var err),
            $"tools/call sobre '{nombre}' devolvió error de protocolo: {(err.ValueKind == JsonValueKind.Object ? err.ToString() : "")}");
        Assert.True(doc.RootElement.TryGetProperty("result", out _));
    }
}
