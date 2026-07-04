using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate del corpus de casos límite de comunidad (eval/community-edge-cases) en .NET puro:
/// ejecuta el mismo pipeline que run.mjs pero in-process (SqlFileLoader -> InputAnalyzer ->
/// GraphExporter --columns) y compara las aristas DERIVES_FROM/READS_FROM concretas contra el
/// *.expected.json de cada caso, reportando FALTA/SOBRA por arista - el equivalente xUnit de
/// una comprobación que run.mjs nunca hizo (solo comprobaba que el pipeline no crashease).
/// </summary>
public class CommunityEdgeCaseGateTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "community-edge-case-gate-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Sube desde el bin de tests hasta la raíz del toolkit (la carpeta que contiene eval/community-edge-cases).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "community-edge-cases", "run.mjs")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontró eval/community-edge-cases/run.mjs subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }

    public sealed record EdgeCase(string Name, string ExpectedJsonPath, string[] SqlFiles);

    public static IEnumerable<object[]> Cases()
    {
        var root = RepoRoot();
        var corpus = Path.Combine(root, "eval", "community-edge-cases");
        (string Name, string ExpectedRel, string[] FilesRel)[] cases =
        [
            ("merge", "dml-advanced/merge.expected.json", ["dml-advanced/merge.sql"]),
            ("merge-with-output", "dml-advanced/merge-with-output.expected.json", ["dml-advanced/merge-with-output.sql"]),
            ("recursive-cte", "cte-recursive/recursive-cte.expected.json", ["cte-recursive/recursive-cte.sql"]),
            ("window", "window-functions/window.expected.json", ["window-functions/window.sql"]),
            ("union-view", "set-ops/union-view.expected.json", ["set-ops/union-view.sql"]),
            ("lineage-chain", "lineage-chain/lineage-chain.expected.json",
                ["lineage-chain/01-base-table.sql", "lineage-chain/02-view-level1.sql", "lineage-chain/03-view-level2.sql", "lineage-chain/04-view-level3.sql"]),
            ("dynamic-sql-complex", "dynamic-sql/dynamic-sql-complex.expected.json", ["dynamic-sql/quotename-case-coalesce.sql"]),
        ];
        foreach (var c in cases)
            yield return [new EdgeCase(c.Name, Path.Combine(corpus, c.ExpectedRel), c.FilesRel.Select(f => Path.Combine(corpus, f)).ToArray())];
    }

    private sealed record ExpectedDerivesFrom(string From, string To, string Logic);
    private sealed record ExpectedReadsFrom(string Source, string Target, Dictionary<string, string> Properties);
    private sealed record ExpectedEdges(List<ExpectedDerivesFrom> DerivesFrom, List<ExpectedReadsFrom> ReadsFrom);

    private static ExpectedEdges LoadExpected(string path)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        var derives = new List<ExpectedDerivesFrom>();
        foreach (var e in doc.GetProperty("derives_from").EnumerateArray())
            derives.Add(new ExpectedDerivesFrom(e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!, e.GetProperty("logic").GetString()!));

        var reads = new List<ExpectedReadsFrom>();
        foreach (var e in doc.GetProperty("reads_from").EnumerateArray())
        {
            var props = new Dictionary<string, string>();
            foreach (var p in e.GetProperty("properties").EnumerateObject())
                props[p.Name] = p.Value.GetString() ?? "";
            reads.Add(new ExpectedReadsFrom(e.GetProperty("source").GetString()!, e.GetProperty("target").GetString()!, props));
        }
        return new ExpectedEdges(derives, reads);
    }

    private static string PropsKey(IEnumerable<KeyValuePair<string, string>> props) =>
        string.Join(",", props.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    private static Dictionary<string, string> ToStringProps(Dictionary<string, object> props)
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in props)
            result[k] = v?.ToString() ?? "";
        return result;
    }

    private static Dictionary<string, int> Tally(IEnumerable<string> keys)
    {
        var tally = new Dictionary<string, int>();
        foreach (var key in keys)
            tally[key] = tally.GetValueOrDefault(key) + 1;
        return tally;
    }

    private static void DiffMultisets(Dictionary<string, int> expected, Dictionary<string, int> actual, string label, List<string> discrepancies)
    {
        foreach (var (key, count) in expected)
        {
            var have = actual.GetValueOrDefault(key);
            for (var i = have; i < count; i++)
                discrepancies.Add($"FALTA {label}  {key}");
        }
        foreach (var (key, count) in actual)
        {
            var want = expected.GetValueOrDefault(key);
            for (var i = want; i < count; i++)
                discrepancies.Add($"SOBRA {label}  {key}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_MatchesExpectedEdges(EdgeCase edgeCase)
    {
        var inputPath = Path.Combine(NewTempDir(), "input.json");
        Assert.Equal(0, SqlFileLoader.Run("CommunityCasesDB", inputPath, edgeCase.SqlFiles));
        var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);

        var expected = LoadExpected(edgeCase.ExpectedJsonPath);

        var actualDerives = graph.Relationships.Where(r => r.Type == "DERIVES_FROM").ToList();
        var actualReads = graph.Relationships.Where(r => r.Type == "READS_FROM").ToList();

        var expectedDerivesKeys = Tally(expected.DerivesFrom.Select(e => $"{e.From} <- {e.To} | {e.Logic}"));
        var actualDerivesKeys = Tally(actualDerives.Select(r =>
            $"{r.StartNodeId} <- {r.EndNodeId} | {(r.Properties.TryGetValue("logic", out var l) ? l : "")}"));

        var expectedReadsKeys = Tally(expected.ReadsFrom.Select(e => $"{e.Source} -> {e.Target} | {PropsKey(e.Properties)}"));
        var actualReadsKeys = Tally(actualReads.Select(r =>
            $"{r.StartNodeId} -> {r.EndNodeId} | {PropsKey(ToStringProps(r.Properties))}"));

        var discrepancies = new List<string>();
        DiffMultisets(expectedDerivesKeys, actualDerivesKeys, "DERIVES_FROM", discrepancies);
        DiffMultisets(expectedReadsKeys, actualReadsKeys, "READS_FROM", discrepancies);

        Assert.True(discrepancies.Count == 0,
            $"Gate community-edge-cases [{edgeCase.Name}]: DERIVES_FROM esperado={expected.DerivesFrom.Count} obtenido={actualDerives.Count}, " +
            $"READS_FROM esperado={expected.ReadsFrom.Count} obtenido={actualReads.Count}, discrepancias={discrepancies.Count}\n" +
            string.Join("\n", discrepancies));
    }
}
