using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

/// <summary>
/// Covers the MCP tool handlers (<see cref="McpTools"/>) directly - no process, no
/// stdio - plus one end-to-end smoke test that launches the real "mcp" subcommand
/// and talks JSON-RPC to it over stdin/stdout, so the transport itself is exercised
/// at least once. The test corpus is synthesized in-memory from small CREATE
/// PROCEDURE statements (same pattern as SqliteExporterTests), never a file on the
/// author's machine.
/// </summary>
public class McpTests : IDisposable
{
    private const string Db = "TestDb";
    private const int ReaderCount = 15;

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                File.Delete(f);
    }

    // ── test corpus ─────────────────────────────────────────────────────────
    //
    //  dbo.HubTable        <- SELECT -- dbo.Reader00 .. dbo.Reader14 (15 readers:
    //                          a hub with many downstream dependents, for the
    //                          ambiguous-name / limit / budget tests)
    //  dbo.Alpha -CALLS-> dbo.Beta -CALLS-> dbo.Gamma -READS_FROM-> dbo.GammaTable
    //                          (a call chain, for multi-hop upstream/downstream)
    //  dbo.UniqueOne        (unrelated, for the exact-match resolve_object test)

    private string BuildDb()
    {
        var sources = new List<(string Name, string Sql)>();
        for (var i = 0; i < ReaderCount; i++)
        {
            var name = $"dbo.Reader{i:00}";
            sources.Add((name, $"CREATE PROCEDURE {name} AS BEGIN SELECT Id FROM dbo.HubTable; END"));
        }
        sources.Add(("dbo.Alpha", "CREATE PROCEDURE dbo.Alpha AS BEGIN EXEC dbo.Beta; END"));
        sources.Add(("dbo.Beta", "CREATE PROCEDURE dbo.Beta AS BEGIN EXEC dbo.Gamma; END"));
        sources.Add(("dbo.Gamma", "CREATE PROCEDURE dbo.Gamma AS BEGIN SELECT Id FROM dbo.GammaTable; END"));
        sources.Add(("dbo.UniqueOne", "CREATE PROCEDURE dbo.UniqueOne AS BEGIN SELECT 1; END"));

        var results = sources.Select(s => SqlAnalyzer.AnalyzeObject($"{Db}::{s.Name}", s.Sql)).ToList();
        foreach (var r in results)
            Assert.Null(r.Error);

        var graph = GraphExporter.Build(results, includeColumns: false);
        var dbPath = Path.Combine(Path.GetTempPath(), $"mcp-test-{Guid.NewGuid():n}.db");
        _tempFiles.Add(dbPath);
        SqliteExporter.Write(graph, dbPath, Db, "TestProj");
        return dbPath;
    }

    private SqliteConnection OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        return conn;
    }

    // ── resolve_object ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveObject_PartialName_ReturnsRankedMatches_RespectsDefaultLimit()
    {
        using var conn = OpenReadOnly(BuildDb());

        var result = McpTools.ResolveObject(conn, "Reader", limit: 10);

        Assert.Equal(ReaderCount, (int)result["total"]!);
        var matches = (List<Dictionary<string, object?>>)result["matches"]!;
        Assert.Equal(10, matches.Count); // default limit clamps the 15 readers
        Assert.True((bool)result["truncated"]!);
        Assert.DoesNotContain("exact", result.Keys); // no single exact match at this needle
        Assert.All(matches, m => Assert.Contains("Reader", (string)m["name"]!));
    }

    [Fact]
    public void ResolveObject_ExactName_SetsExactTrue()
    {
        using var conn = OpenReadOnly(BuildDb());

        var result = McpTools.ResolveObject(conn, "dbo.UniqueOne", limit: 10);

        Assert.True((bool)result["exact"]!);
        var matches = (List<Dictionary<string, object?>>)result["matches"]!;
        Assert.Equal("dbo.UniqueOne", matches[0]["name"]);
        Assert.EndsWith("dbo.UniqueOne", (string)matches[0]["id"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveObject_NoMatches_ReturnsEmpty()
    {
        using var conn = OpenReadOnly(BuildDb());

        var result = McpTools.ResolveObject(conn, "Zzzznonexistent12345", limit: 10);

        Assert.Equal(0, (int)result["total"]!);
        Assert.Empty((List<Dictionary<string, object?>>)result["matches"]!);
        Assert.DoesNotContain("truncated", result.Keys);
        Assert.DoesNotContain("exact", result.Keys);
    }

    [Fact]
    public void ResolveObject_BlankName_Throws()
    {
        using var conn = OpenReadOnly(BuildDb());
        Assert.Throws<McpToolException>(() => McpTools.ResolveObject(conn, "  ", limit: 10));
    }

    // ── impact ───────────────────────────────────────────────────────────────

    [Fact]
    public void Impact_Downstream_FindsCallersTransitively()
    {
        using var conn = OpenReadOnly(BuildDb());
        var gammaId = ResolveOne(conn, "dbo.Gamma");

        var hop1 = McpTools.Impact(conn, gammaId, "downstream", depth: 1, limit: 50);
        var hop1Items = (List<Dictionary<string, object?>>)hop1["affected"]!;
        Assert.Contains(hop1Items, i => ((string)i["name"]!).EndsWith("Beta"));
        Assert.DoesNotContain(hop1Items, i => ((string)i["name"]!).EndsWith("Alpha"));

        var hop2 = McpTools.Impact(conn, gammaId, "downstream", depth: 2, limit: 50);
        var hop2Items = (List<Dictionary<string, object?>>)hop2["affected"]!;
        Assert.Contains(hop2Items, i => ((string)i["name"]!).EndsWith("Alpha") && (int)i["hops"]! == 2);
    }

    [Fact]
    public void Impact_Upstream_FindsDependenciesTransitively()
    {
        using var conn = OpenReadOnly(BuildDb());
        var alphaId = ResolveOne(conn, "dbo.Alpha");

        var hop1 = McpTools.Impact(conn, alphaId, "upstream", depth: 1, limit: 50);
        var hop1Items = (List<Dictionary<string, object?>>)hop1["affected"]!;
        Assert.Contains(hop1Items, i => ((string)i["name"]!).EndsWith("Beta"));
        Assert.DoesNotContain(hop1Items, i => ((string)i["name"]!).EndsWith("Gamma"));

        var hop2 = McpTools.Impact(conn, alphaId, "upstream", depth: 2, limit: 50);
        var hop2Items = (List<Dictionary<string, object?>>)hop2["affected"]!;
        Assert.Contains(hop2Items, i => ((string)i["name"]!).EndsWith("Gamma") && (int)i["hops"]! == 2);
    }

    [Fact]
    public void Impact_Limit_TruncatesAndReportsTotal()
    {
        using var conn = OpenReadOnly(BuildDb());
        var hubId = ResolveOne(conn, "dbo.HubTable");

        var result = McpTools.Impact(conn, hubId, "downstream", depth: 1, limit: 5);
        var items = (List<Dictionary<string, object?>>)result["affected"]!;

        Assert.Equal(5, items.Count);
        Assert.Equal(ReaderCount, (int)result["total"]!);
        Assert.True((bool)result["truncated"]!);
    }

    [Fact]
    public void Impact_UnknownId_Throws()
    {
        using var conn = OpenReadOnly(BuildDb());
        Assert.Throws<McpToolException>(() => McpTools.Impact(conn, "TestDb::dbo.DoesNotExist", "downstream", 1, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Impact_DepthOutOfRange_Throws(int depth)
    {
        using var conn = OpenReadOnly(BuildDb());
        var alphaId = ResolveOne(conn, "dbo.Alpha");
        Assert.Throws<McpToolException>(() => McpTools.Impact(conn, alphaId, "downstream", depth, 50));
    }

    [Fact]
    public void Impact_InvalidDirection_Throws()
    {
        using var conn = OpenReadOnly(BuildDb());
        var alphaId = ResolveOne(conn, "dbo.Alpha");
        Assert.Throws<McpToolException>(() => McpTools.Impact(conn, alphaId, "sideways", 1, 50));
    }

    // ── empty-result honesty (a bare [] must never read as "nothing depends on this") ──

    [Fact]
    public void Impact_EmptyUpstreamOnTable_CarriesConcreteReason()
    {
        using var conn = OpenReadOnly(BuildDb());
        var hubId = ResolveOne(conn, "dbo.HubTable");

        // dbo.HubTable has 15 incoming READS_FROM edges but none outgoing of the
        // traversed types - upstream must come back empty, but with a real reason,
        // not silence that reads as "nothing depends on this table".
        var result = McpTools.Impact(conn, hubId, "upstream", depth: 1, limit: 50);

        Assert.Empty((List<Dictionary<string, object?>>)result["affected"]!);
        // La lista de tipos sale del contrato, no reescrita aquí: si se reescribiera, este
        // test dejaría de verificar el mensaje y pasaría a fijar una copia que se desincroniza.
        Assert.Equal(
            $"sin aristas {string.Join("/", StoreSchema.ImpactEdgeTypes)} salientes de este nodo.",
            result["reason"]);
    }

    [Fact]
    public void Impact_EmptyUpstreamOnTable_HintsAtDownstream()
    {
        using var conn = OpenReadOnly(BuildDb());
        var hubId = ResolveOne(conn, "dbo.HubTable");

        var result = McpTools.Impact(conn, hubId, "upstream", depth: 1, limit: 50);

        Assert.True(result.ContainsKey("hint"));
        var hint = (string)result["hint"]!;
        Assert.Contains("direction=downstream", hint);
        Assert.Contains(ReaderCount.ToString(), hint); // the real count, not a guess
    }

    [Fact]
    public void Impact_NonEmptyResult_HasNoReasonOrHint()
    {
        using var conn = OpenReadOnly(BuildDb());
        var hubId = ResolveOne(conn, "dbo.HubTable");

        var result = McpTools.Impact(conn, hubId, "downstream", depth: 1, limit: 50);

        Assert.DoesNotContain("reason", result.Keys);
        Assert.DoesNotContain("hint", result.Keys);
    }

    // ── response budget gate ────────────────────────────────────────────────

    [Fact]
    public void Impact_MostConnectedNode_DefaultParams_FitsResponseBudget()
    {
        using var conn = OpenReadOnly(BuildDb());

        // "Most connected" restricted to the edge types impact() actually traverses -
        // otherwise a schema/admin node with many unrelated edges would win and the
        // test would stop exercising the thing it's meant to stress.
        string mostConnectedId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id FROM (" +
                "  SELECT dst id FROM edges WHERE type IN ('CALLS','READS_FROM','WRITES_TO','DERIVES_FROM','READS_COLUMN','WRITES_COLUMN')" +
                ") GROUP BY id ORDER BY COUNT(*) DESC LIMIT 1";
            mostConnectedId = (string)cmd.ExecuteScalar()!;
        }

        // Default direction/depth/limit, exactly as a client omitting optional args would call it.
        var result = McpTools.Impact(conn, mostConnectedId, "downstream", depth: 1, limit: 50);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"impact() on the most-connected node ({mostConnectedId}) serialized to {bytes.Length} bytes, " +
            $"over the {McpTools.ResponseBudgetBytes}-byte budget.");
    }

    [Fact]
    public void ResolveObject_MostAmbiguousName_FitsResponseBudget()
    {
        using var conn = OpenReadOnly(BuildDb());

        var result = McpTools.ResolveObject(conn, "Reader", limit: 10);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"resolve_object('Reader') serialized to {bytes.Length} bytes, over the {McpTools.ResponseBudgetBytes}-byte budget.");
    }

    private static string ResolveOne(SqliteConnection conn, string needle)
    {
        var result = McpTools.ResolveObject(conn, needle, limit: 10);
        var matches = (List<Dictionary<string, object?>>)result["matches"]!;
        return (string)matches.Single(m => (string)m["name"]! == needle)["id"]!;
    }

    // ── end-to-end smoke: real process, real stdio, real JSON-RPC ──────────

    [Fact]
    public void EndToEnd_StdioProcess_InitializeToolsListAndToolsCall()
    {
        var dbPath = BuildDb();
        var repoRoot = CorpusManifest.FindRepoRoot(AppContext.BaseDirectory)
                       ?? throw new InvalidOperationException("No se encontró la raíz del repo desde el directorio de pruebas.");
        var dllPath = Path.Combine(repoRoot, "src", "TSqlParser", "bin", "Release", "net10.0", "TSqlParser.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"No existe '{dllPath}' - compila con 'dotnet build -c Release' antes de este test.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\" mcp --store \"{dbPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        try
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            proc.StandardInput.Flush();
            var initLine = ReadLineWithTimeout(proc);
            using var initDoc = JsonDocument.Parse(initLine);
            Assert.Equal("2.0", initDoc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("tsql-lineage-mcp", initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

            // Notification: per JSON-RPC 2.0 (no "id"), must draw no response line.
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            proc.StandardInput.Flush();

            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            proc.StandardInput.Flush();
            var listLine = ReadLineWithTimeout(proc);
            using var listDoc = JsonDocument.Parse(listLine);
            var tools = listDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains("resolve_object", tools);
            Assert.Contains("impact", tools);

            var callRequest = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"resolve_object","arguments":{"name":"dbo.UniqueOne","limit":5}}}""";
            proc.StandardInput.WriteLine(callRequest);
            proc.StandardInput.Flush();
            var callLine = ReadLineWithTimeout(proc);
            using var callDoc = JsonDocument.Parse(callLine);
            var callResult = callDoc.RootElement.GetProperty("result");
            Assert.False(callResult.GetProperty("isError").GetBoolean());
            var toolText = callResult.GetProperty("content")[0].GetProperty("text").GetString()!;
            using var toolPayload = JsonDocument.Parse(toolText);
            Assert.True(toolPayload.RootElement.GetProperty("exact").GetBoolean());

            proc.StandardInput.Close();
            Assert.True(proc.WaitForExit(10_000), "El proceso mcp no terminó tras cerrar stdin.");
            Assert.Equal(0, proc.ExitCode);
        }
        finally
        {
            // Belt-and-braces: an assertion failure above must not leave the child
            // holding the temp .db file locked for Dispose()'s cleanup pass.
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
    }

    private static string ReadLineWithTimeout(Process proc)
    {
        var task = proc.StandardOutput.ReadLineAsync();
        if (!task.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("El proceso mcp no respondió a tiempo. stderr: " + proc.StandardError.ReadToEnd());
        return task.Result ?? throw new InvalidOperationException("El proceso mcp cerró stdout sin responder. stderr: " + proc.StandardError.ReadToEnd());
    }
}
