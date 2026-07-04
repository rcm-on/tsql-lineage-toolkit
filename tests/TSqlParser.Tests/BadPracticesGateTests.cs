using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate del corpus de malas prácticas (eval/bad-practices) en .NET puro: ejecuta el
/// mismo pipeline que run.sh pero in-process (SqlFileLoader -> InputAnalyzer ->
/// GraphExporter --columns -> RiskAnalyzer) y compara los hallazgos contra el
/// ground-truth expected-findings.json, reportando FALTAN (falso negativo), SOBRAN
/// (falso positivo) y desajustes de severidad/categoría - el equivalente xUnit de
/// evaluate.mjs. Corpus actual: 38 hallazgos esperados en 24 componentes.
/// </summary>
public class BadPracticesGateTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "badpractices-gate-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Sube desde el bin de tests hasta la raíz del toolkit (la carpeta que contiene eval/bad-practices).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "bad-practices", "expected-findings.json")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontró eval/bad-practices/expected-findings.json subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }

    private sealed record ExpectedFinding(string Rule, string Sev, string Cat);
    private sealed record ExpectedComponent(string Component, List<ExpectedFinding> Expected);

    private static List<ExpectedComponent> LoadGroundTruth(string path)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        var components = new List<ExpectedComponent>();
        foreach (var c in doc.GetProperty("components").EnumerateArray())
        {
            var expected = new List<ExpectedFinding>();
            if (c.TryGetProperty("expected", out var exp))
                foreach (var e in exp.EnumerateArray())
                    expected.Add(new ExpectedFinding(
                        e.GetProperty("rule").GetString()!,
                        e.GetProperty("sev").GetString()!,
                        e.GetProperty("cat").GetString()!));
            components.Add(new ExpectedComponent(c.GetProperty("component").GetString()!, expected));
        }
        return components;
    }

    [Fact]
    public void Corpus_MatchesExpectedFindings()
    {
        var root = RepoRoot();
        var sqlDir = Path.Combine(root, "eval", "bad-practices", "sql");
        var sqlFiles = Directory.GetFiles(sqlDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(sqlFiles);

        // Mismo pipeline que run.sh, in-process: from-sql -> analyze -> graph --columns.
        var inputPath = Path.Combine(NewTempDir(), "input.json");
        Assert.Equal(0, SqlFileLoader.Run("BadPracticesDB", inputPath, sqlFiles));
        var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);

        var findings = RiskAnalyzer.Analyze(graph);
        var actual = new Dictionary<string, Dictionary<string, (string Sev, string Cat)>>();
        foreach (var f in findings)
        {
            if (!actual.TryGetValue(f.Component, out var rules))
                actual[f.Component] = rules = new();
            rules[f.Rule] = (f.Sev, f.Cat);
        }

        var spec = LoadGroundTruth(Path.Combine(root, "eval", "bad-practices", "expected-findings.json"));

        // Mismo contraste que evaluate.mjs: FALTAN / SOBRAN / SEV-CAT por componente,
        // más componentes con hallazgos que no están en el ground-truth.
        var discrepancies = new List<string>();
        var okCount = 0;
        var seenComponents = new HashSet<string>();
        foreach (var c in spec)
        {
            seenComponents.Add(c.Component);
            actual.TryGetValue(c.Component, out var got);
            got ??= new();
            foreach (var e in c.Expected)
            {
                if (!got.TryGetValue(e.Rule, out var g))
                    discrepancies.Add($"FALTA   {c.Component}: {e.Rule} (esperado {e.Sev}/{e.Cat})");
                else if (e.Sev != g.Sev || e.Cat != g.Cat)
                    discrepancies.Add($"SEV/CAT {c.Component}: {e.Rule} esperado {e.Sev}/{e.Cat}, obtenido {g.Sev}/{g.Cat}");
                else
                    okCount++;
            }
            foreach (var (rule, g) in got)
                if (!c.Expected.Any(e => e.Rule == rule))
                    discrepancies.Add($"SOBRA   {c.Component}: {rule} ({g.Sev}/{g.Cat}) -- no esperado");
        }
        foreach (var (component, rules) in actual)
            if (!seenComponents.Contains(component))
                foreach (var (rule, g) in rules)
                    discrepancies.Add($"SOBRA   {component}: {rule} ({g.Sev}/{g.Cat}) -- componente fuera del ground-truth");

        Assert.True(discrepancies.Count == 0,
            $"Gate bad-practices: OK={okCount}, discrepancias={discrepancies.Count}\n{string.Join("\n", discrepancies)}");
        Assert.Equal(spec.Sum(c => c.Expected.Count), okCount);
    }
}
