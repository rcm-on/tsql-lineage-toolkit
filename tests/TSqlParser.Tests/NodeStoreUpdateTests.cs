using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers <see cref="NodeStoreExporter.Update"/>: the incremental counterpart to
/// <see cref="NodeStoreExporter.Write"/> that only rewrites the
/// objects/**/shared/** files whose content actually changed (per
/// manifest.json content_hash / on-disk comparison), GCs files for
/// removed objects and orphaned shared nodes, and always refreshes
/// model.json/manifest.json/index.json.
/// </summary>
public class NodeStoreUpdateTests : IDisposable
{
    private const string Db = "TestDb";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string ProcASql =
        "CREATE PROCEDURE dbo.ProcA AS BEGIN INSERT INTO dbo.TableX (Id) VALUES (1); END";

    private const string ProcAModifiedSql =
        "CREATE PROCEDURE dbo.ProcA AS BEGIN INSERT INTO dbo.TableX (Id) VALUES (1); INSERT INTO dbo.TableZ (Id) VALUES (2); END";

    private const string ProcBSql =
        "CREATE PROCEDURE dbo.ProcB AS BEGIN INSERT INTO dbo.TableY (Id) VALUES (1); END";

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nodestore-update-tests", Guid.NewGuid().ToString("n"));
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

    private static IEnumerable<string> AllFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>index.json content with meta.generated_at removed, so two stores generated at different times can be compared.</summary>
    private static string NormalizedIndexJson(string path)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(doc.GetRawText())!;
        var meta = JsonSerializer.Deserialize<Dictionary<string, object>>(((JsonElement)dict["meta"]).GetRawText())!;
        meta.Remove("generated_at");
        dict["meta"] = meta;
        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    [Fact]
    public void Update_NoChange_LeavesAllFilesUnchanged()
    {
        var graph = BuildGraph(("ProcA", ProcASql), ("ProcB", ProcBSql));
        var store = NewTempDir();
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);

        var beforeFiles = AllFiles(store).ToList();
        var beforeContent = beforeFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(store, f)));

        var stats = NodeStoreExporter.Update(graph, store, Db, JsonOptions);

        Assert.Equal(0, stats.ObjectsWritten);
        Assert.Equal(0, stats.ObjectsRemoved);
        Assert.Equal(0, stats.SharedWritten);
        Assert.Equal(0, stats.SharedRemoved);
        Assert.Equal(2, stats.ObjectsUnchanged);

        var afterFiles = AllFiles(store).ToList();
        Assert.Equal(beforeFiles, afterFiles);
        foreach (var f in beforeFiles)
        {
            if (f == "index.json")
                continue; // generated_at always refreshed
            Assert.Equal(beforeContent[f], File.ReadAllText(Path.Combine(store, f)));
        }
    }

    [Fact]
    public void Update_SingleObjectChange_OnlyRewritesThatObjectAndItsNewSharedNode()
    {
        var graph1 = BuildGraph(("ProcA", ProcASql), ("ProcB", ProcBSql));
        var store = NewTempDir();
        NodeStoreExporter.Write(graph1, store, Db, JsonOptions);

        var beforeFiles = AllFiles(store).ToList();
        var beforeContent = beforeFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(store, f)));

        // ProcA now also writes to a brand-new table (TableZ).
        var graph2 = BuildGraph(("ProcA", ProcAModifiedSql), ("ProcB", ProcBSql));
        var stats = NodeStoreExporter.Update(graph2, store, Db, JsonOptions);

        Assert.Equal(1, stats.ObjectsWritten);   // ProcA changed
        Assert.Equal(1, stats.ObjectsUnchanged); // ProcB untouched
        Assert.Equal(0, stats.ObjectsRemoved);
        // shared/tables/TableZ is new; the shared INSERT Action node also picks
        // up a new `refs[ProcA]` entry for the TableZ insert.
        Assert.True(stats.SharedWritten >= 1);
        Assert.Equal(0, stats.SharedRemoved);

        var afterFiles = AllFiles(store).ToList();
        var afterContent = afterFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(store, f)));

        // ProcB's object file and its shared TableY are untouched. (Each object now
        // has both object.json and nav.json under objects/, so match object.json.)
        var procBFile = beforeFiles.Single(f => f.StartsWith("objects/") && f.Contains("ProcB") && f.EndsWith("object.json"));
        Assert.Equal(beforeContent[procBFile], afterContent[procBFile]);
        var procBNavFile = beforeFiles.Single(f => f.StartsWith("objects/") && f.Contains("ProcB") && f.EndsWith("nav.json"));
        Assert.Equal(beforeContent[procBNavFile], afterContent[procBNavFile]);

        var tableYFile = beforeFiles.Single(f => f.StartsWith("shared/tables/") && f.Contains("tablex") == false && f.Contains("TableY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeContent[tableYFile], afterContent[tableYFile]);

        // A new shared file for TableZ was added.
        var newSharedFiles = afterFiles.Except(beforeFiles).ToList();
        Assert.Contains(newSharedFiles, f => f.StartsWith("shared/tables/") && f.Contains("TableZ", StringComparison.OrdinalIgnoreCase));

        // manifest.json reflects the new content_hash for ProcA only.
        var manifestBefore = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(beforeContent["manifest.json"])!;
        var manifestAfter = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(afterContent["manifest.json"])!;
        var procAId = manifestAfter.Keys.Single(k => k.Contains("ProcA"));
        var procBId = manifestAfter.Keys.Single(k => k.Contains("ProcB"));
        Assert.NotEqual(
            manifestBefore[procAId].GetProperty("content_hash").GetString(),
            manifestAfter[procAId].GetProperty("content_hash").GetString());
        Assert.Equal(
            manifestBefore[procBId].GetProperty("content_hash").GetString(),
            manifestAfter[procBId].GetProperty("content_hash").GetString());
    }

    [Fact]
    public void Update_ObjectRemoved_GcsItsDirAndOrphanedSharedNode()
    {
        var graph1 = BuildGraph(("ProcA", ProcASql), ("ProcB", ProcBSql));
        var store = NewTempDir();
        NodeStoreExporter.Write(graph1, store, Db, JsonOptions);

        var beforeFiles = AllFiles(store).ToList();
        var procBDir = beforeFiles.First(f => f.StartsWith("objects/") && f.Contains("ProcB"));
        var procBObjectDir = Path.GetDirectoryName(procBDir)!.Replace('\\', '/');
        var tableYFile = beforeFiles.Single(f => f.StartsWith("shared/tables/") && f.Contains("TableY", StringComparison.OrdinalIgnoreCase));

        // ProcB (and its only reference to TableY) disappears from the input.
        var graph2 = BuildGraph(("ProcA", ProcASql));
        var stats = NodeStoreExporter.Update(graph2, store, Db, JsonOptions);

        Assert.Equal(1, stats.ObjectsRemoved);
        Assert.Equal(1, stats.SharedRemoved);
        Assert.Equal(0, stats.ObjectsWritten); // ProcA's own object.json is unchanged
        Assert.Equal(1, stats.ObjectsUnchanged);

        Assert.False(Directory.Exists(Path.Combine(store, procBObjectDir)));
        Assert.False(File.Exists(Path.Combine(store, tableYFile)));

        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(store, "manifest.json")))!;
        Assert.DoesNotContain(manifest.Keys, k => k.Contains("ProcB"));
    }

    private const string ProcCSql =
        "CREATE PROCEDURE dbo.ProcC AS BEGIN EXEC dbo.ProcA; INSERT INTO dbo.TableX (Id) VALUES (9); END";

    private const string ProcDynamicSql =
        "CREATE PROCEDURE dbo.ProcD AS BEGIN DECLARE @s NVARCHAR(MAX); SET @s = 'SELECT Id FROM dbo.TableX'; EXEC(@s); IF 1 = 1 INSERT INTO dbo.TableX (Id) VALUES (1); END";

    [Fact]
    public void Write_ModelJson_RollsUpComplexityAndDynamicSqlStatsPerObject_AndFkCountPerTable()
    {
        var graph = BuildGraph(("dbo.ProcD", ProcDynamicSql));
        var store = NewTempDir();
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);

        var model = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(store, "model.json")));
        var procD = model.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("label").GetString() == "SqlObject");

        // The single EXEC(@s) step is dynamic SQL; the procedure has at least 2 steps total.
        Assert.True(procD.GetProperty("total_steps").GetInt32() >= 2);
        Assert.Equal(1, procD.GetProperty("dynamic_sql_steps").GetInt32());
        // The IF branch gives this procedure a cyclomatic_complexity above the trivial baseline.
        Assert.True(procD.GetProperty("cyclomatic_complexity").GetInt32() >= 1);

        var tableX = model.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("label").GetString() == "Table");
        // No table schema/DDL was supplied to this test graph, so no FK can be detected.
        Assert.Equal(0, tableX.GetProperty("fk_out_count").GetInt32());
    }

    private static readonly HashSet<string> NavEdgeTypes = new()
    {
        "CALLS", "WRITES_TO", "READS_FROM", "AFFECTS", "FK_TO",
    };

    // A step that reads a real table must produce READS_FROM even when (a) the read
    // lives in a cursor's SELECT body (incl. UNION branches) or (b) the row lands in
    // a #temp/@table-variable target. Both were silently dropped before; both are real
    // reads for impact analysis ("what reads dbo.SourceX?"). `FOR SYSTEM_TIME AS OF`
    // is included to confirm the temporal clause itself never blocked the read.
    [Theory]
    [InlineData("cursor-simple", "CREATE PROCEDURE dbo.P AS BEGIN DECLARE c CURSOR FOR SELECT Id FROM dbo.SourceA; OPEN c; CLOSE c; END", "dbo.sourcea")]
    [InlineData("cursor-union", "CREATE PROCEDURE dbo.P AS BEGIN DECLARE c CURSOR FOR SELECT Id FROM dbo.SourceA UNION SELECT Id FROM dbo.SourceB; OPEN c; END", "dbo.sourcea,dbo.sourceb")]
    [InlineData("insert-temp", "CREATE PROCEDURE dbo.P AS BEGIN CREATE TABLE #t (Id int); INSERT INTO #t (Id) SELECT b.Id FROM dbo.SourceB AS b; END", "dbo.sourceb")]
    [InlineData("insert-temp-systime", "CREATE PROCEDURE dbo.P AS BEGIN CREATE TABLE #t (Id int); INSERT INTO #t (Id) SELECT b.Id FROM dbo.SourceB FOR SYSTEM_TIME AS OF '2020-01-01' AS b; END", "dbo.sourceb")]
    [InlineData("select-into-temp", "CREATE PROCEDURE dbo.P AS BEGIN SELECT b.Id INTO #t FROM dbo.SourceB AS b; END", "dbo.sourceb")]
    public void ReadsFrom_SurvivesCursorBodyAndTempTarget(string label, string sql, string expectedCsv)
    {
        var graph = BuildGraph(("dbo.P", sql));
        var reads = graph.Relationships
            .Where(r => r.Type == "READS_FROM")
            .Select(r => r.EndNodeId.Split(":table:")[^1])
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var expected = expectedCsv.Split(',').OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, reads);
    }

    [Fact]
    public void Write_EmitsNavJson_WithOnlyNavEdges_AndNavToNavPaths()
    {
        // ProcC EXECs ProcA (a CALLS object->object) and writes TableX. Names are
        // schema-qualified so the EXEC target "dbo.ProcA" resolves to the registered
        // object (a bare "ProcA" id would not match and no CALLS edge would form).
        var graph = BuildGraph(("dbo.ProcA", ProcASql), ("dbo.ProcC", ProcCSql));
        var store = NewTempDir();
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);

        // Every object has a sibling nav.json next to its object.json.
        var objectFiles = AllFiles(store).Where(f => f.EndsWith("/object.json")).ToList();
        Assert.NotEmpty(objectFiles);
        foreach (var objFile in objectFiles)
        {
            var navFile = objFile.Replace("/object.json", "/nav.json");
            Assert.True(File.Exists(Path.Combine(store, navFile)), $"missing {navFile}");

            var nav = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(store, navFile)));
            foreach (var edge in nav.GetProperty("edges_out").EnumerateArray())
                Assert.Contains(edge.GetProperty("type").GetString(), NavEdgeTypes);
        }

        // manifest.json carries nav_file for each object.
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(store, "manifest.json")))!;
        foreach (var entry in manifest.Values)
            Assert.EndsWith("/nav.json", entry.GetProperty("nav_file").GetString());

        // ProcC's nav.json has the CALLS edge to ProcA, and its `path` points at
        // ProcA's nav.json (the cheap entry point), not its object.json.
        var procCNav = AllFiles(store).Single(f => f.Contains("ProcC") && f.EndsWith("/nav.json"));
        var procCDoc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(store, procCNav)));
        var callsEdge = procCDoc.GetProperty("edges_out").EnumerateArray()
            .Single(e => e.GetProperty("type").GetString() == "CALLS");
        Assert.EndsWith("/nav.json", callsEdge.GetProperty("path").GetString());
        Assert.Contains("ProcA", callsEdge.GetProperty("path").GetString());

        // A WRITES_TO edge (target is a shared Table, no nav.json) keeps a normal
        // shared path, not a nav.json path.
        var writesEdge = procCDoc.GetProperty("edges_out").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "WRITES_TO");
        Assert.StartsWith("shared/", writesEdge.GetProperty("path").GetString());
    }

    [Fact]
    public void Update_MatchesFreshWrite_ExceptGeneratedAt()
    {
        var graph1 = BuildGraph(("ProcA", ProcASql), ("ProcB", ProcBSql));
        var storeA = NewTempDir();
        NodeStoreExporter.Write(graph1, storeA, Db, JsonOptions);

        var graph2 = BuildGraph(("ProcA", ProcAModifiedSql), ("ProcB", ProcBSql));

        // storeA: incremental update from graph1's store.
        NodeStoreExporter.Update(graph2, storeA, Db, JsonOptions);

        // storeB: full regeneration straight from graph2.
        var storeB = NewTempDir();
        NodeStoreExporter.Write(graph2, storeB, Db, JsonOptions);

        Assert.Equal(AllFiles(storeB).ToList(), AllFiles(storeA).ToList());

        foreach (var f in AllFiles(storeB))
        {
            var pathA = Path.Combine(storeA, f);
            var pathB = Path.Combine(storeB, f);
            if (f == "index.json")
            {
                Assert.Equal(NormalizedIndexJson(pathB), NormalizedIndexJson(pathA));
            }
            else
            {
                Assert.Equal(File.ReadAllText(pathB), File.ReadAllText(pathA));
            }
        }
    }
}
