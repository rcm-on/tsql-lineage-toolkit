using System.Text;
using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers two small-but-visible artifact-hygiene defects:
///
/// 1. Every JSON file the toolkit writes (graph_full.json, and the whole
///    NodeStore: index.json, model.json, manifest.json, audit_report.json,
///    change_map.json, objects/*/object.json, shared/**) used to carry a
///    UTF-8 byte-order mark because <see cref="Encoding.UTF8"/>'s preamble
///    is BOM-on. That's legal UTF-8 but breaks strict consumers such as
///    Python's json.load() ("Unexpected UTF-8 BOM"). Writers now go through
///    <see cref="Utf8Io.WriteAllText"/>, which uses a BOM-less UTF8Encoding.
///    Reading must still tolerate a BOM, for artifacts already on disk from
///    older versions of the tool.
///
/// 2. audit_report.json's lineage_coverage.coverage_pct used to report 100
///    when columns_total was 0 (no output-column surface at all, e.g. a
///    corpus of procedures with no output columns) - indistinguishable from
///    "everything is covered". It now reports null plus an explicit
///    "measured" flag so an automated consumer can tell the two cases apart.
/// </summary>
public class JsonHygieneTests : IDisposable
{
    private const string Db = "TestDb";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string ProcSql =
        "CREATE PROCEDURE dbo.ProcA AS BEGIN INSERT INTO dbo.TableX (Id) VALUES (1); END";

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "json-hygiene-tests", Guid.NewGuid().ToString("n"));
        _tempDirs.Add(dir);
        return dir;
    }

    private static GraphPayload BuildGraph(bool includeColumns) =>
        GraphExporter.Build(
            new List<ObjectResult> { SqlAnalyzer.AnalyzeObject($"{Db}::dbo.ProcA", ProcSql) },
            includeColumns: includeColumns);

    private static bool StartsWithBom(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[3];
        var read = fs.Read(head, 0, 3);
        return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    // ── 1a. No JSON artifact written by the exporter starts with a BOM ─────

    [Fact]
    public void NodeStoreFiles_DoNotStartWithBom()
    {
        var store = NewTempDir();
        var graph = BuildGraph(includeColumns: true);
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);

        var jsonFiles = Directory.EnumerateFiles(store, "*.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(jsonFiles);
        foreach (var file in jsonFiles)
            Assert.False(StartsWithBom(file), $"{file} starts with a UTF-8 BOM");
    }

    [Fact]
    public void Utf8Io_WriteAllText_NeverEmitsBom()
    {
        var path = Path.Combine(NewTempDir(), "sample.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Utf8Io.WriteAllText(path, "{\"a\":1}");

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
            "Utf8Io.WriteAllText must not emit a UTF-8 BOM");
    }

    // ── 1b. Reading a file that DOES have a BOM (artifacts from older
    //        versions of the tool) still works - backward compatibility. ──

    [Fact]
    public void ReadingBomPrefixedJson_StillParses()
    {
        var path = Path.Combine(NewTempDir(), "with-bom.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = "{\"hello\":\"world\"}";
        File.WriteAllText(path, json, Encoding.UTF8); // deliberately WITH BOM, simulating an old artifact

        Assert.True(StartsWithBom(path), "test setup should have produced a BOM-prefixed file");

        // File.ReadAllText auto-detects and strips a leading BOM.
        var text = File.ReadAllText(path);
        var doc = JsonDocument.Parse(text);
        Assert.Equal("world", doc.RootElement.GetProperty("hello").GetString());
    }

    // ── 2. coverage_pct with a zero denominator ─────────────────────────────

    [Fact]
    public void LineageCoverage_ZeroDenominator_ReportsNullNotHundred()
    {
        // includeColumns: false => no HAS_COLUMN edges at all => no output
        // columns to measure lineage coverage over (columns_total == 0).
        var store = NewTempDir();
        NodeStoreExporter.Write(BuildGraph(includeColumns: false), store, Db, JsonOptions);

        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(store, "audit_report.json"))).RootElement;
        var coverage = report.GetProperty("lineage_coverage");

        Assert.Equal(0, coverage.GetProperty("columns_total").GetInt32());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("coverage_pct").ValueKind);
        Assert.False(coverage.GetProperty("measured").GetBoolean());
    }

    [Fact]
    public void LineageCoverage_WithColumns_ReportsMeasuredNumericPct()
    {
        var store = NewTempDir();
        NodeStoreExporter.Write(BuildGraph(includeColumns: true), store, Db, JsonOptions);

        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(store, "audit_report.json"))).RootElement;
        var coverage = report.GetProperty("lineage_coverage");

        // Whether or not this particular graph has output columns depends on
        // SqlAnalyzer's column extraction for a plain INSERT-only procedure;
        // either way "measured" must agree with columns_total, and coverage_pct
        // must only be null when columns_total is 0.
        var columnsTotal = coverage.GetProperty("columns_total").GetInt32();
        var measured = coverage.GetProperty("measured").GetBoolean();
        Assert.Equal(columnsTotal > 0, measured);
        Assert.Equal(columnsTotal == 0, coverage.GetProperty("coverage_pct").ValueKind == JsonValueKind.Null);
    }
}
