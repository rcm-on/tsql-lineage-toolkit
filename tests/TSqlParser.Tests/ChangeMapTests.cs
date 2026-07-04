using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers <see cref="ChangeMapExporter"/> (change_map.json, Capa 6): workflows from
/// CALLS entry points with per-hop conditionality, cycle handling, impact.via_calls
/// transitive closure and impact.via_data table fanout. Spec: docs/task-change-map.md
/// (Tarea J P1-P7 with the vocabulary drift resolved: conditionality comes from the
/// calling Step's condition_path).
/// </summary>
public class ChangeMapTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "change-map-tests", Guid.NewGuid().ToString("n"));
        _tempDirs.Add(dir);
        return dir;
    }

    private static GraphPayload BuildGraph(params (string Name, string Sql)[] objects)
    {
        var results = objects
            .Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Name}", o.Sql))
            .ToList();
        foreach (var r in results)
            Assert.Null(r.Error);
        return GraphExporter.Build(results, includeColumns: false);
    }

    private static JsonElement Generate(GraphPayload graph) =>
        JsonDocument.Parse(ChangeMapExporter.Generate(graph, new(), JsonOptions)).RootElement;

    private static JsonElement WorkflowOf(JsonElement changeMap, string entryName) =>
        changeMap.GetProperty("workflows").EnumerateArray()
            .Single(w => w.GetProperty("entry_name").GetString() == entryName);

    private static JsonElement ImpactOf(JsonElement changeMap, string plainName) =>
        changeMap.GetProperty("impact").EnumerateObject()
            .Single(p => p.Value.GetProperty("name").GetString() == plainName).Value;

    [Fact]
    public void UnconditionalChain_OneWorkflowTwoHops()
    {
        var cm = Generate(BuildGraph(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN EXEC dbo.ProcC; END"),
            ("dbo.ProcC", "CREATE PROCEDURE dbo.ProcC AS BEGIN SELECT 1 AS X; END")));

        // Only ProcA has in-degree 0 with outgoing CALLS.
        var entries = cm.GetProperty("workflows").EnumerateArray()
            .Select(w => w.GetProperty("entry_name").GetString()).ToList();
        Assert.Equal(new[] { "dbo.ProcA" }, entries);

        var wf = WorkflowOf(cm, "dbo.ProcA");
        Assert.Equal("PROCEDURE", wf.GetProperty("entry_type").GetString());
        var paths = wf.GetProperty("paths").EnumerateArray().ToList();
        Assert.Single(paths);
        var hops = paths[0].GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal(2, hops.Count);
        Assert.All(hops, h =>
        {
            Assert.False(h.GetProperty("conditional").GetBoolean());
            Assert.Equal(JsonValueKind.Null, h.GetProperty("condition").ValueKind);
        });
        Assert.EndsWith("dbo.ProcB", hops[0].GetProperty("to").GetString());
        Assert.EndsWith("dbo.ProcC", hops[1].GetProperty("to").GetString());

        // via_calls closure from ProcA: ProcB depth 1, ProcC depth 2.
        var viaCalls = ImpactOf(cm, "dbo.ProcA").GetProperty("via_calls").EnumerateArray().ToList();
        Assert.Equal(2, viaCalls.Count);
        Assert.Equal(1, viaCalls.Single(v => v.GetProperty("object").GetString() == "dbo.ProcB").GetProperty("depth").GetInt32());
        Assert.Equal(2, viaCalls.Single(v => v.GetProperty("object").GetString() == "dbo.ProcC").GetProperty("depth").GetInt32());
    }

    [Fact]
    public void CallUnderIf_HopIsConditionalWithConditionText()
    {
        var cm = Generate(BuildGraph(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA @x INT AS BEGIN IF @x = 1 EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 1 AS X; END")));

        var hop = WorkflowOf(cm, "dbo.ProcA").GetProperty("paths")[0].GetProperty("hops")[0];
        Assert.True(hop.GetProperty("conditional").GetBoolean());
        Assert.Contains("@x = 1", hop.GetProperty("condition").GetString());

        var reached = ImpactOf(cm, "dbo.ProcA").GetProperty("via_calls")[0];
        Assert.True(reached.GetProperty("conditional").GetBoolean());
        Assert.Contains("@x = 1", reached.GetProperty("condition_text").GetString());
    }

    [Fact]
    public void Cycle_PathIsCutAndViaCallsMarksCycleEntry()
    {
        // A -> B -> C -> B: back-edge into B (not into the entry).
        var cm = Generate(BuildGraph(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN EXEC dbo.ProcC; END"),
            ("dbo.ProcC", "CREATE PROCEDURE dbo.ProcC AS BEGIN EXEC dbo.ProcB; END")));

        var hops = WorkflowOf(cm, "dbo.ProcA").GetProperty("paths")[0].GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal(3, hops.Count);
        var last = hops[^1];
        Assert.EndsWith("dbo.ProcB", last.GetProperty("cycle_back_to").GetString());

        var viaCalls = ImpactOf(cm, "dbo.ProcA").GetProperty("via_calls").EnumerateArray().ToList();
        var b = viaCalls.Single(v => v.GetProperty("object").GetString() == "dbo.ProcB");
        Assert.True(b.TryGetProperty("cycle_entry", out var ce) && ce.GetBoolean());
        // C is reached once at depth 2 and not re-expanded.
        Assert.Equal(2, viaCalls.Count);
    }

    [Fact]
    public void ViaData_WrittenTableListsItsReaders()
    {
        var cm = Generate(BuildGraph(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"),
            ("dbo.ReaderTwo", "CREATE PROCEDURE dbo.ReaderTwo AS BEGIN SELECT Id FROM dbo.T; END")));

        var viaData = ImpactOf(cm, "dbo.Writer").GetProperty("via_data").EnumerateArray().ToList();
        var t = viaData.Single(v => v.GetProperty("table").GetString()!.EndsWith("dbo.T", StringComparison.OrdinalIgnoreCase));
        var consumers = t.GetProperty("consumers").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(new[] { "dbo.ReaderOne", "dbo.ReaderTwo" }, consumers);
    }

    [Fact]
    public void Trigger_IsNotAWorkflowEntry()
    {
        var cm = Generate(BuildGraph(
            ("dbo.TR_T", "CREATE TRIGGER dbo.TR_T ON dbo.T AFTER INSERT AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 1 AS X; END")));

        // The trigger is excluded from the workflows CALLS subgraph in v1 (P5); its
        // callee has no other callers but no outgoing CALLS either, so no workflows.
        Assert.Empty(cm.GetProperty("workflows").EnumerateArray());
        Assert.DoesNotContain(cm.GetProperty("impact").EnumerateObject(),
            p => p.Value.GetProperty("name").GetString() == "dbo.TR_T");
    }

    [Fact]
    public void WriteAndUpdate_BothEmitChangeMapAtStoreRoot()
    {
        var graph = BuildGraph(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"));
        var store = NewTempDir();

        NodeStoreExporter.Write(graph, store, Db, JsonOptions);
        var path = Path.Combine(store, "change_map.json");
        Assert.True(File.Exists(path));
        var first = File.ReadAllText(path);
        Assert.Contains("workflows", first);

        // Update must refresh it too (denormalized cache, always rewritten).
        File.WriteAllText(path, "{}");
        NodeStoreExporter.Update(graph, store, Db, JsonOptions);
        Assert.Equal(first, File.ReadAllText(path));
    }
}
