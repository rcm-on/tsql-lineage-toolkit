using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Regression suite for the PRINT/RAISERROR severity fix: RAISERROR with severity
/// &lt;= 10 is informational T-SQL (no error, no CATCH, no flow break) and must not
/// be classified as THROW; PRINT is its own tracked action instead of vanishing
/// silently; and an IF/WHILE branch whose only consequence is a PRINT/informational
/// RAISERROR must still produce a Rule + governed Step, instead of disappearing
/// from the flowchart entirely. See docs/ejecucion-canonica for the real-world cases
/// (Ola Hallengren's dbo.CommandExecute, WideWorldImporters'
/// Website.RecordVehicleTemperature) that motivated this.
/// </summary>
public class InformationalOutputTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql, string objectName = "dbo.TestProc")
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static GraphNode? FindNode(GraphPayload graph, Func<GraphNode, bool> pred) =>
        graph.Nodes.FirstOrDefault(pred);

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    [Fact]
    public void RaiseError_LowSeverity_IsNotThrow()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Msg NVARCHAR(200)
            AS
            BEGIN
                RAISERROR(@Msg, 10, 1) WITH NOWAIT
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "THROW"));
        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT"));
    }

    [Fact]
    public void RaiseError_HighSeverity_IsThrow()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Msg NVARCHAR(200)
            AS
            BEGIN
                RAISERROR(@Msg, 16, 1)
            END
            """;
        var graph = BuildGraph(sql);

        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "THROW"));
        Assert.Null(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT"));
    }

    [Fact]
    public void RaiseError_VariableSeverity_FailsClosedAsThrow()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Msg NVARCHAR(200), @Sev INT
            AS
            BEGIN
                RAISERROR(@Msg, @Sev, 1)
            END
            """;
        var graph = BuildGraph(sql);

        // Severity isn't a literal we can evaluate statically - fail closed as an
        // error rather than risk silently swallowing a real one.
        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "THROW"));
        Assert.Null(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT"));
    }

    [Fact]
    public void Print_ProducesStep()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Msg NVARCHAR(200)
            AS
            BEGIN
                PRINT @Msg
            END
            """;
        var graph = BuildGraph(sql);

        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT"));
    }

    [Fact]
    public void IfWithOnlyPrint_ProducesRuleAndGovernedStep()
    {
        // Mirrors WideWorldImporters' Website.RecordVehicleTemperature: an
        // IF @@ROWCOUNT = 0 guard whose only consequence is PRINT. Before this fix,
        // PRINT produced no step at all, so this branch vanished from the flowchart.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @HelpMessage NVARCHAR(200)
            AS
            BEGIN
                UPDATE dbo.Target SET Col1 = 1
                IF @@ROWCOUNT = 0
                BEGIN
                    PRINT N'Warning: No valid sensor data found'
                    PRINT @HelpMessage
                END
            END
            """;
        var graph = BuildGraph(sql);

        var rule = FindNode(graph, n => n.Labels.Contains("Rule"));
        Assert.NotNull(rule);

        var printStep = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT");
        Assert.NotNull(printStep);

        var governs = FindRel(graph, "GOVERNS", r => r.StartNodeId == rule!.Id && r.EndNodeId == printStep!.Id);
        Assert.NotNull(governs);
    }

    /// <summary>
    /// Control flow nests arbitrarily deep in the wild (sp_Blitz measures
    /// nesting_level up to 9) - the invisible-branch fix can't just work at one
    /// level. PRINT at the bottom of WHILE -> IF -> IF must produce a step whose
    /// condition_path lists all three enclosing conditions, in order, and whose
    /// nesting_level equals that path's length - the same invariant every other
    /// step (UPDATE, INSERT, ...) already satisfies.
    /// </summary>
    [Fact]
    public void DeeplyNestedIf_InsideWhile_PrintCarriesFullConditionPath()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @i INT, @Flag INT, @Other INT, @Msg NVARCHAR(200)
            AS
            BEGIN
                WHILE @i < 10
                BEGIN
                    IF @Flag = 1
                    BEGIN
                        IF @Other = 2
                        BEGIN
                            PRINT @Msg
                        END
                    END
                    SET @i = @i + 1
                END
            END
            """;
        var graph = BuildGraph(sql);

        var printStep = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT");
        Assert.NotNull(printStep);

        var conditionPath = Assert.IsAssignableFrom<System.Collections.IEnumerable>(printStep!.Properties["condition_path"])
            .Cast<object>().Select(o => o.ToString()!).ToList();
        Assert.Equal(3, conditionPath.Count);
        Assert.StartsWith("WHILE:", conditionPath[0]);
        Assert.StartsWith("IF:", conditionPath[1]);
        Assert.StartsWith("IF:", conditionPath[2]);

        var nestingLevel = Convert.ToInt32(printStep.Properties["nesting_level"]);
        Assert.Equal(conditionPath.Count, nestingLevel);

        // Global invariant Ramón verified across all three corpora: every step
        // with a non-empty condition_path has nesting_level > 0, and the deepest
        // nesting_level in the graph never exceeds the longest condition_path.
        foreach (var step in graph.Nodes.Where(n => n.Labels.Contains("Step")))
        {
            var path = ((System.Collections.IEnumerable)step.Properties["condition_path"]).Cast<object>().ToList();
            var level = Convert.ToInt32(step.Properties["nesting_level"]);
            Assert.Equal(path.Count, level);
            if (path.Count > 0)
                Assert.True(level > 0);
        }
    }

    /// <summary>
    /// PRINT inside a CATCH block that is itself inside a WHILE loop - a second
    /// nesting shape (TRY/CATCH, not IF) that must not lose the step either.
    /// </summary>
    [Fact]
    public void PrintInsideCatch_InsideWhile_ProducesStep()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @i INT
            AS
            BEGIN
                WHILE @i < 10
                BEGIN
                    BEGIN TRY
                        SELECT 1 FROM dbo.Target
                    END TRY
                    BEGIN CATCH
                        PRINT 'error'
                    END CATCH
                    SET @i = @i + 1
                END
            END
            """;
        var graph = BuildGraph(sql);

        var printStep = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "PRINT");
        Assert.NotNull(printStep);

        var conditionPath = ((System.Collections.IEnumerable)printStep!.Properties["condition_path"]).Cast<object>().ToList();
        var nestingLevel = Convert.ToInt32(printStep.Properties["nesting_level"]);
        Assert.Equal(conditionPath.Count, nestingLevel);
        Assert.True(nestingLevel > 0);
    }
}
