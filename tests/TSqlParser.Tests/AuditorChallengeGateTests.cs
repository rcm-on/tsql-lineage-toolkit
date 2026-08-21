using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// .NET equivalent of eval/auditor-challenge/verify.mjs: docs/claude-audit-report.md and
/// docs/gemini-audit-report.md make prose claims (with hand-cited figures) about
/// DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad (WWI) and its impact on
/// Website.Customers/Website.Suppliers via lineage_path.json. This test re-derives the same
/// figures from a freshly regenerated NodeStore every run, so a future change (Tarea A/
/// AdventureWorks, a fix to a gap in docs/extraction-gaps.md, a real regeneration of out/)
/// breaks it audibly instead of leaving the audit reports silently stale.
///
/// Same in-process mechanism as ViewLineageCatalogTests, but this test extracts the WHOLE
/// WideWorldImporters database (ObjectExtractor.Run with no --object/--like filter, so every
/// procedure/function/trigger/view) instead of a fixed list of views, because the metrics it
/// checks (a procedure's cyclomatic_complexity/dynamic-SQL resolution, its WRITES_TO fan-out,
/// the resulting column-lineage impact on 3 unrelated views) depend on the full call/write
/// graph, not a single object in isolation. Pipeline: ObjectExtractor.Run -> TableSchemaExtractor
/// .RunAll -> InputAnalyzer.Analyze -> GraphExporter.Build(includeColumns:true) ->
/// NodeStoreExporter.Write (same as `dotnet run -- ... --columns --nodestore`) - then reads
/// model.json/manifest.json/nav.json/lineage_path.json exactly like verify.mjs reads them from
/// out/graph_full.nodes, so a parallel `node eval/auditor-challenge/verify.mjs &lt;this
/// temp dir&gt;` run is expected to report the identical numbers (JS/C# parity).
///
/// Needs localhost\SQLEXPRESS (or TSQLPARSER_SQL_SERVER) with WideWorldImporters restored -
/// not portable to a plain CI runner, hence the LiveSql trait (`dotnet test --filter
/// Category!=LiveSql` skips this class).
/// </summary>
[Trait("Category", "LiveSql")]
public class AuditorChallengeGateTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private static string Server =>
        Environment.GetEnvironmentVariable("TSQLPARSER_SQL_SERVER") ?? @"localhost\SQLEXPRESS";

    private const string Database = "WideWorldImporters";
    private const string ProcName = "DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad";
    private const string ProcId = $"{Database}::{ProcName}";

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "auditor-challenge-gate-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>The 17 tables the report claims are written (via ALTER, nav.json WRITES_TO) -
    /// "no 18th hidden table" - copied verbatim from verify.mjs's EXPECTED_WRITTEN_TABLES.</summary>
    private static readonly HashSet<string> ExpectedWrittenTables = new(StringComparer.Ordinal)
    {
        "application.cities", "application.countries", "application.deliverymethods",
        "application.paymentmethods", "application.people", "application.stateprovinces",
        "application.transactiontypes", "purchasing.suppliercategories", "purchasing.suppliers",
        "sales.buyinggroups", "sales.customercategories", "sales.customers",
        "warehouse.coldroomtemperatures", "warehouse.colors", "warehouse.packagetypes",
        "warehouse.stockgroups", "warehouse.stockitems",
    };

    [Fact]
    public void WideWorldImporters_MatchesAuditorChallengeSnapshot()
    {
        var tempDir = NewTempDir();
        var inputPath = Path.Combine(tempDir, "input.json");

        // Extract the WHOLE database (no objectNames/likePattern filter) plus every base
        // table's DDL, exactly like `TSqlParser extract WideWorldImporters input.json --tables`.
        var extractResult = ObjectExtractor.Run(Database, inputPath, Server);
        Assert.True(extractResult == 0, $"No se pudo extraer objetos de {Server}/{Database}: {SqlConnections.LastError ?? "sin error de conexión registrado"}");
        Assert.True(TableSchemaExtractor.RunAll(Database, inputPath, Server) == 0,
            $"No se pudieron extraer tablas base de {Server}/{Database}: {SqlConnections.LastError ?? "sin error de conexión registrado"}");

        var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // Same call NodeStoreExporter.Write makes from Program.cs under --nodestore.
        var nodeStorePath = Path.Combine(tempDir, "wwi.nodes");
        NodeStoreExporter.Write(graph, nodeStorePath, Database, jsonOptions);

        // ── Hotspot #3 del informe: complejidad + punto ciego de SQL dinámico ───
        var model = ReadJson(Path.Combine(nodeStorePath, "model.json"));
        var procNode = model.GetProperty("nodes").EnumerateArray()
            .FirstOrDefault(n => n.TryGetProperty("id", out var id) && id.GetString() == ProcId);
        Assert.True(procNode.ValueKind != JsonValueKind.Undefined,
            $"No encuentro {ProcId} en model.json - ¿es el nodestore de WWI?");

        var cc = procNode.GetProperty("cyclomatic_complexity").GetInt32();
        Assert.True(cc == 19, $"cyclomatic_complexity esperado=19 (snapshot WWI) obtenido={cc}");

        var dynSteps = procNode.GetProperty("dynamic_sql_steps").GetInt32();
        var unresolved = procNode.GetProperty("unresolved_dynamic_sql_steps").GetInt32();
        Assert.True(unresolved == 0,
            "unresolved_dynamic_sql_steps esperado=0 (gaps 5.1 QUOTENAME y 5.2 NCHAR/CASE/COALESCE cerrados) " +
            $"obtenido={unresolved}/{dynSteps} (era 34/34 sin fix, 17/34 tras solo QUOTENAME)");

        // ── Las 17 tablas escritas (ALTER, vía nav.json) - "ninguna tabla 18ª oculta" ──
        var manifest = ReadJson(Path.Combine(nodeStorePath, "manifest.json"));
        Assert.True(manifest.TryGetProperty(ProcId, out var procEntry),
            $"{ProcId}: sin entrada en manifest.json");
        var navRelPath = procEntry.GetProperty("nav_file").GetString()!;
        var nav = ReadJson(Path.Combine(nodeStorePath, navRelPath));

        var writtenTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in nav.GetProperty("edges_out").EnumerateArray())
        {
            if (edge.GetProperty("type").GetString() != "WRITES_TO")
                continue;
            var to = edge.GetProperty("to").GetString()!;
            writtenTables.Add(to.Replace($"{Database}:table:", ""));
        }

        var missing = ExpectedWrittenTables.Where(t => !writtenTables.Contains(t)).ToList();
        var extra = writtenTables.Where(t => !ExpectedWrittenTables.Contains(t)).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            "WRITES_TO esperado=exactamente las 17 tablas conocidas (sin tabla 18ª oculta) " +
            $"obtenido n={writtenTables.Count}" +
            (missing.Count > 0 ? $" FALTAN={string.Join(",", missing)}" : "") +
            (extra.Count > 0 ? $" SOBRAN={string.Join(",", extra)}" : ""));

        // ── Impacto en lineage_path.json de las 3 vistas de Website ────────────
        AssertLineageCoverage(nodeStorePath, manifest, $"{Database}::Website.Customers", writtenTables,
            expectedImpacted: 14, expectedTotal: 14);
        AssertLineageCoverage(nodeStorePath, manifest, $"{Database}::Website.Suppliers", writtenTables,
            expectedImpacted: 12, expectedTotal: 12);
        AssertLineageCoverage(nodeStorePath, manifest, $"{Database}::Website.VehicleTemperatures", writtenTables,
            expectedImpacted: 0, expectedTotal: 6);
    }

    /// <summary>
    /// Same computation as verify.mjs's lineageCoverage(): for every output column of
    /// <paramref name="viewObjId"/>, its lineage_path.json entry carries `roots` as
    /// "schema.table.column" strings - the column is "impacted" if any root's "schema.table"
    /// (first two dot-segments, lowercased) is one of the tables the procedure writes to.
    /// </summary>
    private static void AssertLineageCoverage(string nodeStorePath, JsonElement manifest, string viewObjId,
        HashSet<string> writtenTables, int expectedImpacted, int expectedTotal)
    {
        Assert.True(manifest.TryGetProperty(viewObjId, out var entry),
            $"{viewObjId}: sin entrada en manifest.json (¿no extraído?)");
        var objectFileRelPath = entry.GetProperty("object_file").GetString()!;
        var lineagePathRelPath = Path.Combine(Path.GetDirectoryName(objectFileRelPath)!, "lineage_path.json")
            .Replace('\\', '/');
        var fullPath = Path.Combine(nodeStorePath, lineagePathRelPath);
        Assert.True(File.Exists(fullPath), $"{viewObjId}: sin lineage_path.json");

        var lp = ReadJson(fullPath);
        var total = 0;
        var impacted = 0;
        foreach (var col in lp.EnumerateObject())
        {
            total++;
            var roots = col.Value.GetProperty("roots").EnumerateArray().Select(r => r.GetString()!);
            var isImpacted = roots.Any(root =>
            {
                var parts = root.Split('.');
                var table = string.Join(".", parts.Take(2)).ToLowerInvariant();
                return writtenTables.Contains(table);
            });
            if (isImpacted)
                impacted++;
        }

        Assert.True(impacted == expectedImpacted && total == expectedTotal,
            $"{viewObjId}: columnas impactadas esperado={expectedImpacted}/{expectedTotal} obtenido={impacted}/{total}");
    }

    private static JsonElement ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)).RootElement;
}
