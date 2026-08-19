using System.Text.Json;

namespace Parser.Graph;

/// <summary>
/// Lo que todo sink necesita, calculado una vez. Antes lo recalculaba cada bloque de
/// exportación de Program.cs por su cuenta: el nombre de la base y la derivación de la
/// ruta de salida estaban repetidos cuatro veces.
/// </summary>
public sealed class ExportContext
{
    public required string GraphOutputPath { get; init; }
    public required string Database { get; init; }
    public required string Project { get; init; }
    public required JsonSerializerOptions JsonOptions { get; init; }

    /// <summary>"out/graph_full.json" + ".db" -&gt; "out/graph_full.db".</summary>
    public string PathWith(string extension) =>
        GraphOutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? GraphOutputPath[..^5] + extension
            : GraphOutputPath + extension;
}

/// <param name="OutputPath">Lo que se escribió, para quien tenga que hacer algo después.</param>
/// <param name="Summary">Línea informativa, tal cual va a stdout.</param>
public sealed record SinkResult(string OutputPath, string Summary);

/// <summary>
/// Un formato de salida del grafo. Formato nuevo = clase nueva registrada en
/// <see cref="GraphSinks.Default"/>; Program.cs no se toca.
/// </summary>
public interface IGraphSink
{
    /// <summary>Bandera del CLI que lo activa, p. ej. "--sqlite".</summary>
    string Flag { get; }

    /// <summary>Extensión que se le añade a la ruta base, p. ej. ".db".</summary>
    string Extension { get; }

    SinkResult Write(GraphPayload graph, ExportContext ctx);
}
