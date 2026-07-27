using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Regression suite for the "EXECUTE @variable" cross-database sp_executesql pattern
/// Ola Hallengren's maintenance scripts use heavily:
///
///   SET @CurrentDatabase_sp_executesql = QUOTENAME(@CurrentDatabaseName) + '.sys.sp_executesql'
///   ...
///   EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand
///
/// Root cause (confirmed against the real corpus in C:\temp\corpus-ola-src, see
/// AstWalker.cs's InsertStatement case): ExecTarget/ResolveExecLiteral already classified
/// a variable-named sp_executesql call as dynamic SQL correctly, in any nesting depth -
/// the WHILE/nested-IF-loop hypothesis this suite exercises (tests 2 and 3 below) is
/// FALSE, confirmed with a synthetic repro before touching any code. The real gap was
/// "INSERT INTO Target (...) EXECUTE @var ...": ScriptDom parses that whole statement as
/// a single InsertStatement whose InsertSource is an ExecuteInsertSource, which never
/// reaches the ExecuteStatement switch case at all - so the INSERT-shaped half of every
/// sp_executesql call (the majority of occurrences in IndexOptimize.sql and
/// DatabaseIntegrityCheck.sql) was silently dropped: no dynamic-SQL flag, no
/// DynamicSqlCount, no dynamic_sql text feeding ResolveDynamicSqlLinks.
/// </summary>
public class DynamicExecViaVariableTests
{
    private const string Db = "TestDb";

    private static ObjectResult Analyze(string sql, string objectName = "dbo.TestProc")
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql);
        Assert.Null(result.Error);
        return result;
    }

    private static GraphNode? FindNode(GraphPayload graph, Func<GraphNode, bool> pred) =>
        graph.Nodes.FirstOrDefault(pred);

    /// <summary>1. A bare "EXECUTE @variable_con_nombre_de_proc @stmt = @cmd" (the
    /// cross-database sp_executesql-via-variable pattern) must be marked dynamic SQL.</summary>
    [Fact]
    public void ExecuteViaSpExecutesqlVariable_IsMarkedDynamic()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @CurrentDatabase_sp_executesql NVARCHAR(MAX)
                DECLARE @CurrentCommand NVARCHAR(MAX)
                DECLARE @CurrentDatabaseName NVARCHAR(MAX)

                SET @CurrentDatabase_sp_executesql = QUOTENAME(@CurrentDatabaseName) + '.sys.sp_executesql'
                EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand
            END
            """;
        var result = Analyze(sql);
        Assert.Equal(1, result.DynamicSqlCount);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });
        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "EXEC");
        Assert.NotNull(step);
        Assert.True((bool)step!.Properties["is_dynamic_sql"]);
    }

    /// <summary>2. The same pattern repeated N times inside a WHILE loop must be detected
    /// all N times - this is the shape of IndexOptimize.sql's per-database cursor loop.
    /// Half the repetitions here are plain EXECUTE, half are "INSERT INTO #T (...) EXEC
    /// @var ..." (the real corpus's dominant shape and the actual root cause), so this
    /// also locks in the INSERT-EXEC fix, not just the loop-repetition hypothesis.</summary>
    [Fact]
    public void RepeatedPatternInsideWhileLoop_AllOccurrencesDetected()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @CurrentDatabase_sp_executesql NVARCHAR(MAX)
                DECLARE @CurrentCommand NVARCHAR(MAX)
                DECLARE @CurrentDatabaseName NVARCHAR(MAX)

                SET @CurrentDatabase_sp_executesql = QUOTENAME(@CurrentDatabaseName) + '.sys.sp_executesql'

                WHILE 1 = 1
                BEGIN
                    EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand

                    INSERT INTO #Objects (ObjectID)
                    EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand

                    EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand

                    INSERT INTO #Indexes (IndexID)
                    EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand
                END
            END
            """;
        var result = Analyze(sql);
        Assert.Equal(4, result.DynamicSqlCount);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });
        var dynSteps = graph.Nodes.Where(n =>
            n.Labels.Contains("Step")
            && n.Properties.TryGetValue("is_dynamic_sql", out var d) && d is true).ToList();
        Assert.Equal(4, dynSteps.Count);
    }

    /// <summary>2b (nested IF branches). The same pattern repeated across nested/sibling
    /// IF branches - not just a flat loop body - must also be detected every time.</summary>
    [Fact]
    public void RepeatedPatternInsideNestedIfBranches_AllOccurrencesDetected()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @CurrentDatabase_sp_executesql NVARCHAR(MAX)
                DECLARE @CurrentCommand NVARCHAR(MAX)
                DECLARE @CurrentDatabaseName NVARCHAR(MAX)

                SET @CurrentDatabase_sp_executesql = QUOTENAME(@CurrentDatabaseName) + '.sys.sp_executesql'

                WHILE 1 = 1
                BEGIN
                    IF 1 = 1
                    BEGIN
                        EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand

                        INSERT INTO #Objects (ObjectID)
                        EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand

                        IF 2 = 2
                        BEGIN
                            INSERT INTO #Indexes (IndexID)
                            EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand
                        END
                    END
                    ELSE
                    BEGIN
                        INSERT INTO #Stats (StatID)
                        EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand
                    END
                END
            END
            """;
        var result = Analyze(sql);
        Assert.Equal(4, result.DynamicSqlCount);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });
        var dynSteps = graph.Nodes.Where(n =>
            n.Labels.Contains("Step")
            && n.Properties.TryGetValue("is_dynamic_sql", out var d) && d is true).ToList();
        Assert.Equal(4, dynSteps.Count);
    }

    /// <summary>3. An "EXECUTE @var" that is NOT sp_executesql (a real procedure name
    /// held in a variable) must keep behaving exactly as before this fix - locked in
    /// against the pre-fix baseline (DynamicSqlCount=1, is_dynamic_sql=true, ExecCalls
    /// empty; ExecTarget's fallback "default" branch, untouched by this fix, already
    /// treats any non-literal exec target as dynamic since the real callee is
    /// statically unknowable).</summary>
    [Fact]
    public void ExecuteViaVariable_NotSpExecutesql_BehavesAsBefore()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @ProcName SYSNAME = N'dbo.SomeProc'
                EXEC @ProcName @Param1 = 1
            END
            """;
        var result = Analyze(sql);
        Assert.Equal(1, result.DynamicSqlCount);
        Assert.Empty(result.ExecCalls);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });
        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "EXEC");
        Assert.NotNull(step);
        Assert.True((bool)step!.Properties["is_dynamic_sql"]);
    }

    /// <summary>Companion to test 3: "INSERT INTO T (...) EXEC dbo.RealProc ..." (a
    /// literal, non-dynamic procedure name as an INSERT's source) must now surface a
    /// CALLS edge - this specific shape was unreachable before this fix (the INSERT case
    /// never looked at ExecuteInsertSource at all) and is a direct side effect of fixing
    /// the sp_executesql-via-variable gap the same code path shares.</summary>
    [Fact]
    public void InsertExecOfLiteralProcedureName_RecordsExecCall()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO #Results (Col1)
                EXEC dbo.RealProc @Param1 = 1
            END
            """;
        var result = Analyze(sql);
        Assert.Equal(0, result.DynamicSqlCount);
        Assert.Contains("dbo.RealProc", result.ExecCalls);
    }
}
