using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers <see cref="ChangeMapDiff"/> (diff-change-map, the PR "what does this
/// change break?" case): objects_changed/added/removed from manifest content_hash,
/// per-object impact_delta (via_calls/via_data added/removed, newly_affected),
/// workflows_delta, and the --fail-on-new-impact gate. Spec (CLOSED):
/// docs/task-change-map-diff.md ("Tests previstos"). Pattern mirrors
/// <see cref="ChangeMapTests"/>: build graphs in-process, materialize two node
/// stores into temp dirs with NodeStoreExporter.Write, then diff them.
/// </summary>
public class ChangeMapDiffTests : IDisposable
{
    private const string Db = "TestDb";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "change-map-diff-tests", Guid.NewGuid().ToString("n"));
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Materializes a node store from the given objects and returns its dir.</summary>
    private string WriteStore(params (string Name, string Sql)[] objects)
    {
        var results = objects
            .Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Name}", o.Sql))
            .ToList();
        foreach (var r in results)
            Assert.Null(r.Error);
        var graph = GraphExporter.Build(results, includeColumns: false);
        var dir = NewTempDir();
        NodeStoreExporter.Write(graph, dir, Db, JsonOptions);
        return dir;
    }

    /// <summary>Runs the diff into a temp file and returns (parsed JSON, exit code).</summary>
    private (JsonElement Diff, int Exit) RunDiff(string before, string after, bool failOnNewImpact = false)
    {
        var outPath = Path.Combine(NewTempDir(), "diff.json");
        var exit = ChangeMapDiff.Run(before, after, outPath, failOnNewImpact, JsonOptions);
        var diff = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement;
        return (diff, exit);
    }

    private static List<string> Strings(JsonElement arr) =>
        arr.EnumerateArray().Select(e => e.GetString()!).ToList();

    [Fact]
    public void AddedWrite_ProducesViaDataAddedAndNewlyAffected()
    {
        // before: Writer doesn't touch dbo.T; after: Writer INSERTs into dbo.T,
        // which ReaderOne already reads -> via_data_added + newly_affected.
        var before = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));
        var after = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));

        var (diff, exit) = RunDiff(before, after);
        Assert.Equal(0, exit); // plain run always 0

        Assert.Equal(new[] { $"{Db}::dbo.Writer" }, Strings(diff.GetProperty("objects_changed")));
        Assert.Empty(diff.GetProperty("objects_added").EnumerateArray());
        Assert.Empty(diff.GetProperty("objects_removed").EnumerateArray());

        var delta = diff.GetProperty("impact_delta").GetProperty($"{Db}::dbo.Writer");
        var viaDataAdded = delta.GetProperty("via_data_added").EnumerateArray().ToList();
        var t = viaDataAdded.Single(v => v.GetProperty("table").GetString()!.EndsWith("dbo.T", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "dbo.ReaderOne" }, Strings(t.GetProperty("consumers")));
        Assert.Contains("dbo.ReaderOne", Strings(delta.GetProperty("newly_affected")));

        Assert.True(diff.GetProperty("summary").GetProperty("newly_affected_total").GetInt32() >= 1);
        Assert.Contains("dbo", diff.GetProperty("summary").GetProperty("risk_note").GetString());
    }

    [Fact]
    public void AddedExec_ProducesViaCallsAddedWithDepth()
    {
        // before: ProcA calls nothing; after: ProcA EXECs ProcB -> via_calls_added depth 1.
        var before = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 2 AS Y; END"));
        var after = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 2 AS Y; END"));

        var (diff, exit) = RunDiff(before, after);
        Assert.Equal(0, exit);

        var delta = diff.GetProperty("impact_delta").GetProperty($"{Db}::dbo.ProcA");
        var viaCallsAdded = delta.GetProperty("via_calls_added").EnumerateArray().ToList();
        var b = viaCallsAdded.Single(v => v.GetProperty("object").GetString() == "dbo.ProcB");
        Assert.Equal(1, b.GetProperty("depth").GetInt32());
        Assert.False(b.GetProperty("conditional").GetBoolean());
        Assert.Contains("dbo.ProcB", Strings(delta.GetProperty("newly_affected")));

        // A new workflow entry appears (ProcA gains outgoing CALLS, in-degree 0).
        Assert.Contains("dbo.ProcA", Strings(diff.GetProperty("workflows_delta").GetProperty("added")));
    }

    [Fact]
    public void AddedAndRemovedObject_ListedWithoutFalseDeltasElsewhere()
    {
        // ProcStable is byte-identical in both; ProcGone only in before; ProcNew only in after.
        var stableSql = "CREATE PROCEDURE dbo.ProcStable AS BEGIN SELECT 1 AS X; END";
        var before = WriteStore(
            ("dbo.ProcStable", stableSql),
            ("dbo.ProcGone", "CREATE PROCEDURE dbo.ProcGone AS BEGIN SELECT 9 AS Z; END"));
        var after = WriteStore(
            ("dbo.ProcStable", stableSql),
            ("dbo.ProcNew", "CREATE PROCEDURE dbo.ProcNew AS BEGIN SELECT 8 AS W; END"));

        var (diff, _) = RunDiff(before, after);

        Assert.Equal(new[] { $"{Db}::dbo.ProcNew" }, Strings(diff.GetProperty("objects_added")));
        Assert.Equal(new[] { $"{Db}::dbo.ProcGone" }, Strings(diff.GetProperty("objects_removed")));
        // ProcStable didn't change -> not in objects_changed and no delta.
        Assert.DoesNotContain($"{Db}::dbo.ProcStable", Strings(diff.GetProperty("objects_changed")));
        Assert.False(diff.GetProperty("impact_delta").TryGetProperty($"{Db}::dbo.ProcStable", out _));
    }

    [Fact]
    public void NoChanges_EmptyDiffAndExitZeroEvenWithGate()
    {
        var before = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"));
        // Same SQL -> identical stores.
        var after = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"));

        var (diff, exit) = RunDiff(before, after, failOnNewImpact: true);
        Assert.Equal(0, exit); // no new impact -> gate stays green

        Assert.Empty(diff.GetProperty("objects_changed").EnumerateArray());
        Assert.Empty(diff.GetProperty("objects_added").EnumerateArray());
        Assert.Empty(diff.GetProperty("objects_removed").EnumerateArray());
        Assert.Empty(diff.GetProperty("impact_delta").EnumerateObject());
        Assert.Equal(0, diff.GetProperty("summary").GetProperty("newly_affected_total").GetInt32());
        Assert.Equal(JsonValueKind.Null, diff.GetProperty("summary").GetProperty("risk_note").ValueKind);
    }

    [Fact]
    public void ReshapedWorkflow_ReportsNumericPathCounts()
    {
        // ProcA stays the entry in both stores but gains a second branch:
        // 1 path before, 2 after -> reshaped (not added/removed), with
        // paths_before/paths_after as JSON numbers (spec's output format).
        var before = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ProcC", "CREATE PROCEDURE dbo.ProcC AS BEGIN SELECT 2 AS Y; END"));
        var after = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; EXEC dbo.ProcC; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ProcC", "CREATE PROCEDURE dbo.ProcC AS BEGIN SELECT 2 AS Y; END"));

        var (diff, _) = RunDiff(before, after);

        var wf = diff.GetProperty("workflows_delta");
        Assert.Empty(wf.GetProperty("added").EnumerateArray());
        Assert.Empty(wf.GetProperty("removed").EnumerateArray());
        var reshaped = wf.GetProperty("reshaped").EnumerateArray()
            .Single(r => r.GetProperty("entry").GetString() == "dbo.ProcA");
        Assert.Equal(JsonValueKind.Number, reshaped.GetProperty("paths_before").ValueKind);
        Assert.Equal(1, reshaped.GetProperty("paths_before").GetInt32());
        Assert.Equal(2, reshaped.GetProperty("paths_after").GetInt32());
    }

    [Fact]
    public void FailOnNewImpact_ExitsTwoWhenImpactIsNew()
    {
        var before = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));
        var after = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));

        var (_, exit) = RunDiff(before, after, failOnNewImpact: true);
        Assert.Equal(2, exit);
    }
}
