using Parser.Contracts;
using TSqlParser;

namespace ParserGeneral;

/// <summary>
/// Wraps TSqlParser's public pipeline (InputAnalyzer.Analyze + GraphExporter.Build,
/// the same two calls Program.cs makes for its default "input.json -> graph" mode)
/// behind IGraphExtractor, without touching TSqlParser itself.
///
/// Two input shapes are accepted:
///   - "*.json": already an input.json ({ name, sql }[]) - analyzed directly.
///   - "*.sql" (file or directory): built into a temporary input.json via
///     SqlFileLoader.Run (the same helper the "from-sql" CLI subcommand uses),
///     then analyzed the same way. Database name defaults to "Db" since a raw
///     .sql input carries no database name of its own.
/// </summary>
public class TSqlExtractorAdapter : IGraphExtractor
{
    public string Name => "tsql";

    public bool CanHandle(string inputPath) =>
        inputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || inputPath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
        || (Directory.Exists(inputPath) && Directory.EnumerateFiles(inputPath, "*.sql", SearchOption.AllDirectories).Any());

    public GraphPayload Extract(string inputPath) => Extract(inputPath, includeColumns: false);

    public GraphPayload Extract(string inputPath, bool includeColumns)
    {
        var jsonInputPath = inputPath;
        string? tempInputPath = null;
        try
        {
            if (!inputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                tempInputPath = Path.Combine(Path.GetTempPath(), $"parsergeneral-{Guid.NewGuid():N}.input.json");
                var sourcePaths = Directory.Exists(inputPath)
                    ? Directory.EnumerateFiles(inputPath, "*.sql", SearchOption.AllDirectories).ToList()
                    : new List<string> { inputPath };
                var rc = SqlFileLoader.Run("Db", tempInputPath, sourcePaths);
                if (rc != 0)
                    throw new InvalidOperationException($"SqlFileLoader.Run failed for '{inputPath}' (exit {rc}).");
                jsonInputPath = tempInputPath;
            }

            var (results, tableSchemas) = InputAnalyzer.Analyze(jsonInputPath);
            return GraphExporter.Build(results, includeColumns, tableSchemas);
        }
        finally
        {
            if (tempInputPath != null && File.Exists(tempInputPath))
                File.Delete(tempInputPath);
        }
    }
}
