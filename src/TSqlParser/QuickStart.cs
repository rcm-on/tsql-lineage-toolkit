using System.Text.Json;

namespace TSqlParser;

/// <summary>"quickstart": de una base viva a un .db consultable y el comando para conectarle un agente.</summary>
public static class QuickStart
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string database, string server, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        var inputPath = Path.Combine(outDir, "input.json");
        var graphPath = Path.Combine(outDir, "graph_full.json");

        try
        {
            Directory.CreateDirectory(outDir);

            var rc = ObjectExtractor.Run(database, inputPath, server, new List<string>(), null);
            if (rc != 0) return rc;
            rc = TableSchemaExtractor.RunAll(database, inputPath, server);
            if (rc != 0) return rc;

            var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
            var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);
            Utf8Io.WriteAllText(graphPath, JsonSerializer.Serialize(graph, Json));

            var dbName = results.Select(o => o.ObjectName.Split("::", 2)).FirstOrDefault(p => p.Length == 2)?[0] ?? "";
            var ctx = new ExportContext { GraphOutputPath = graphPath, Database = dbName, Project = dbName, JsonOptions = Json };
            var sink = GraphSinks.Default.Single(s => s.Flag == "--sqlite");
            var sinkResult = sink.Write(graph, ctx);

            var ok = results.Count(r => r.Error == null);
            Console.WriteLine($"Objetos analizados: {results.Count} ({ok} ok, {results.Count - ok} con error de parseo)");
            Console.WriteLine($"Grafo: {graph.Nodes.Count} nodos, {graph.Relationships.Count} aristas");
            Console.WriteLine(sinkResult.Summary);
            Console.WriteLine();

            var dbPath = Path.GetFullPath(sinkResult.OutputPath);
            var (command, args) = McpLaunch(Environment.ProcessPath ?? "TSqlParser", typeof(QuickStart).Assembly.Location, dbPath);
            Console.WriteLine(ConfigText(command, args));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"quickstart: {ex.Message}");
            Console.Error.WriteLine("Comprueba el servidor, el nombre de la base y que el usuario tenga VIEW DEFINITION sobre ella.");
            return 1;
        }
    }

    /// <summary>Comando y argumentos que lanzan el servidor MCP: vía `dotnet &lt;dll&gt;` o el apphost directo.</summary>
    public static (string Command, string[] Args) McpLaunch(string processPath, string entryAssembly, string storePath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (processPath, [entryAssembly, "mcp", "--store", storePath])
            : (processPath, ["mcp", "--store", storePath]);

    public static string ConfigText(string command, string[] args)
    {
        var line = "claude mcp add tsql-lineage -- " + string.Join(" ", new[] { command }.Concat(args).Select(Quote));
        var json = JsonSerializer.Serialize(
            new { mcpServers = new Dictionary<string, object> { ["tsql-lineage"] = new { command, args } } }, Json);
        return "Conecta un agente (el servidor MCP no se conecta a SQL Server: lee el fichero .db; " +
               "para refrescarlo, vuelve a ejecutar quickstart).\n\n" +
               "Claude Code:\n  " + line + "\n\n" +
               "Cursor / Claude Desktop (JSON de configuración):\n" + json;
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
}
