using Microsoft.Data.Sqlite;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers <see cref="SqliteExporter.Write"/>: the single queryable SQLite export.
/// Verifies the schema is populated, the promoted audit columns carry their values,
/// referential integrity holds (every edge endpoint is a real node), the props bag
/// stays valid JSON, and the meta table self-identifies the database/project.
/// </summary>
public class SqliteExporterTests : IDisposable
{
    private const string Db = "TestDb";
    private const string Project = "TestProj";

    private const string ProcWrite =
        "CREATE PROCEDURE dbo.ProcWrite AS BEGIN INSERT INTO dbo.TableX (Id) VALUES (1); END";
    private const string ProcDynamic =
        "CREATE PROCEDURE dbo.ProcDynamic AS BEGIN DECLARE @s NVARCHAR(MAX); SET @s = N'SELECT 1'; EXEC(@s); END";
    private const string ProcCursor =
        "CREATE PROCEDURE dbo.ProcCursor AS BEGIN DECLARE c CURSOR FOR SELECT Id FROM dbo.TableY; OPEN c; CLOSE c; END";

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                File.Delete(f);
    }

    private string BuildDb()
    {
        var results = new[]
        {
            ("dbo.ProcWrite", ProcWrite),
            ("dbo.ProcDynamic", ProcDynamic),
            ("dbo.ProcCursor", ProcCursor),
        }.Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Item1}", o.Item2)).ToList();
        foreach (var r in results)
            Assert.Null(r.Error);

        var graph = GraphExporter.Build(results, includeColumns: false);
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite-exporter-test-{Guid.NewGuid():n}.db");
        _tempFiles.Add(dbPath);
        SqliteExporter.Write(graph, dbPath, Db, Project);
        return dbPath;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    private static long ScalarLong(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string? ScalarString(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void Write_PopulatesNodesEdgesAndMeta()
    {
        using var c = Open(BuildDb());

        Assert.True(ScalarLong(c, "SELECT COUNT(*) FROM nodes") > 0);
        Assert.True(ScalarLong(c, "SELECT COUNT(*) FROM edges") > 0);
        Assert.Equal(3, ScalarLong(c, "SELECT COUNT(*) FROM nodes WHERE label='SqlObject'"));

        // meta self-identifies the database + project written at creation time.
        Assert.Equal(Db, ScalarString(c, "SELECT value FROM meta WHERE key='database'"));
        Assert.Equal(Project, ScalarString(c, "SELECT value FROM meta WHERE key='project'"));
        Assert.Equal("graph-sqlite-v1", ScalarString(c, "SELECT value FROM meta WHERE key='format'"));
    }

    [Fact]
    public void Write_ForeignKeysHold_NoOrphanEdges()
    {
        using var c = Open(BuildDb());

        // Every edge endpoint references a real node: foreign_key_check is empty.
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var reader = cmd.ExecuteReader();
        Assert.False(reader.Read(), "foreign_key_check returned a violation");
    }

    [Fact]
    public void Write_PromotesAuditColumns()
    {
        using var c = Open(BuildDb());

        // SqlObject inventory dimension.
        Assert.Equal(3, ScalarLong(c, "SELECT COUNT(*) FROM nodes WHERE label='SqlObject' AND object_type='PROCEDURE'"));
        // Security: the dynamic-SQL proc has at least one is_dynamic_sql step.
        Assert.True(ScalarLong(c, "SELECT COUNT(*) FROM nodes WHERE label='Step' AND is_dynamic_sql=1") >= 1);
        // Robustness: the cursor proc is flagged has_cursor.
        Assert.Equal(1, ScalarLong(c, "SELECT has_cursor FROM nodes WHERE name LIKE '%ProcCursor%'"));
        // Edge action_type promoted off the WRITES_TO into dbo.TableX.
        Assert.True(ScalarLong(c, "SELECT COUNT(*) FROM edges WHERE type='WRITES_TO' AND action_type='INSERT'") >= 1);
    }

    [Fact]
    public void Write_PropsBag_IsValidJson_AndLossless()
    {
        using var c = Open(BuildDb());

        // A Step keeps its full property bag as JSON; json_extract pulls a field
        // that is NOT a promoted column (proves losslessness via the props column).
        var target = ScalarString(c,
            "SELECT json_extract(props,'$.target_name') FROM nodes WHERE label='Step' AND props IS NOT NULL LIMIT 1");
        Assert.False(string.IsNullOrEmpty(target));
    }
}
