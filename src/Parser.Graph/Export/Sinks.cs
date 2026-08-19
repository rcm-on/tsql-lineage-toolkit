using System.Text.Json;

namespace Parser.Graph;

/// <summary>Forma plana { meta, stats, nodes, edges } para Graphify / D3 / vis-network,
/// convertible a Cypher.</summary>
public sealed class GraphifySink : IGraphSink
{
    public string Flag => "--graphify";
    public string Extension => ".graphify.json";

    public SinkResult Write(GraphPayload graph, ExportContext ctx)
    {
        var path = ctx.PathWith(Extension);
        var graphify = GraphifyExporter.ToGraphify(graph, ctx.Database);
        Utf8Io.WriteAllText(path, JsonSerializer.Serialize(graphify, ctx.JsonOptions));
        return new SinkResult(path, $"Graphify: {graphify.Nodes.Count} nodes, {graphify.Edges.Count} edges -> {path}");
    }
}

/// <summary>GraphML (XML) para Gephi / yEd / Cytoscape / NetworkX.</summary>
public sealed class GraphMlSink : IGraphSink
{
    public string Flag => "--graphml";
    public string Extension => ".graphml";

    public SinkResult Write(GraphPayload graph, ExportContext ctx)
    {
        var path = ctx.PathWith(Extension);
        Utf8Io.WriteAllText(path, GraphMlExporter.ToGraphMl(graph));
        return new SinkResult(path, $"GraphML: {graph.Nodes.Count} nodes, {graph.Relationships.Count} edges -> {path}");
    }
}

/// <summary>Almacén de nodos navegable e incremental: un fichero por objeto, para que un
/// agente lea unos pocos ficheros pequeños en vez del grafo entero.</summary>
public sealed class NodeStoreSink : IGraphSink
{
    public string Flag => "--nodestore";
    public string Extension => ".nodes";

    public SinkResult Write(GraphPayload graph, ExportContext ctx)
    {
        var path = ctx.PathWith(Extension);
        var stats = NodeStoreExporter.Write(graph, path, ctx.Database, ctx.JsonOptions);
        return new SinkResult(path,
            $"NodeStore: {stats.Objects} objects, {stats.SharedNodes} shared nodes, {stats.Edges} edges -> {path}");
    }
}

/// <summary>Base SQLite consultable (nodes + edges). Es la que lee el servidor MCP.</summary>
public sealed class SqliteSink : IGraphSink
{
    public string Flag => "--sqlite";
    public string Extension => ".db";

    public SinkResult Write(GraphPayload graph, ExportContext ctx)
    {
        var path = ctx.PathWith(Extension);
        SqliteExporter.Write(graph, path, ctx.Database, ctx.Project);
        return new SinkResult(path,
            $"SQLite: {graph.Nodes.Count} nodes, {graph.Relationships.Count} edges " +
            $"(db={ctx.Database}, project={ctx.Project}) -> {path}");
    }
}

/// <summary>Composition root de los formatos de salida. Una salida nueva se añade aquí y
/// en ningún otro sitio.</summary>
public static class GraphSinks
{
    public static IReadOnlyList<IGraphSink> Default { get; } =
    [
        new GraphifySink(),
        new GraphMlSink(),
        new NodeStoreSink(),
        new SqliteSink(),
    ];
}
