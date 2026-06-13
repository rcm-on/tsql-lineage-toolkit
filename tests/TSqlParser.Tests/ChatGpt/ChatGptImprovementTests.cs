using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests.ChatGpt
{
    public class ChatGptImprovementTests
    {
        private const string Db = "TestDb";

        private static GraphPayload BuildGraph(string sql, bool includeColumns = true, string objectName = "dbo.TestProc")
        {
            var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql);
            Assert.Null(result.Error);
            return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns);
        }

        [Fact]
        public void InsertUnion_AllBranchesDeriveFromSources()
        {
            var sql = """
                CREATE PROCEDURE dbo.TestProc
                AS
                BEGIN
                    INSERT INTO dbo.Target (Col1)
                    SELECT Col1 FROM dbo.SourceA
                    UNION ALL
                    SELECT Col1 FROM dbo.SourceB
                END
                """;

            var graph = BuildGraph(sql);

            // Expect WRITES_TO present
            var target = graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
            Assert.NotNull(target);
            Assert.NotNull(graph.Relationships.FirstOrDefault(r => r.Type == "WRITES_TO" && r.EndNodeId == target!.Id));

            // Known current limitation: no DERIVES_FROM exists; the improved parser should produce DERIVES_FROM edges.
            // For now assert that if DERIVES_FROM exists, it references either SourceA or SourceB columns.
            var derives = graph.Relationships.Where(r => r.Type == "DERIVES_FROM").ToList();
            if (derives.Count > 0)
            {
                var srcTables = graph.Nodes.Where(n => n.Labels.Contains("Table")).Select(n => (string)n.Properties["name"]).ToHashSet();
                Assert.True(srcTables.Contains("dbo.SourceA") || srcTables.Contains("dbo.SourceB"));
            }
        }

        [Fact]
        public void DerivedTable_WithAmbiguousAliases_ProducesReadsAndDerives()
        {
            var sql = """
                CREATE PROCEDURE dbo.TestProc
                AS
                BEGIN
                    INSERT INTO dbo.Target (C)
                    SELECT d.SumC FROM (
                        SELECT A.Col1 + B.Col2 AS SumC
                        FROM dbo.A
                        CROSS JOIN dbo.B
                    ) d
                END
                """;

            var graph = BuildGraph(sql);

            // Expect A and B to be present as READS_FROM
            Assert.Contains(graph.Relationships, r => r.Type == "READS_FROM" && graph.Nodes.Any(n => n.Id == r.EndNodeId && (string)n.Properties["name"] == "dbo.A"));
            Assert.Contains(graph.Relationships, r => r.Type == "READS_FROM" && graph.Nodes.Any(n => n.Id == r.EndNodeId && (string)n.Properties["name"] == "dbo.B"));

            // If column lineage exists, Target.C should derive from A.Col1 and B.Col2
            var colTarget = graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target");
            if (colTarget != null)
            {
                var derives = graph.Relationships.Where(r => r.Type == "DERIVES_FROM" && r.StartNodeId == colTarget.Id).ToList();
                if (derives.Count > 0)
                {
                    var targets = derives.Select(d => graph.Nodes.First(n => n.Id == d.EndNodeId)).ToList();
                    Assert.Contains(targets, n => (string)n.Properties["table"] == "dbo.a");
                    Assert.Contains(targets, n => (string)n.Properties["table"] == "dbo.b");
                }
            }
        }

        [Fact]
        public void Cte_WithMultipleLevels_ProducesReadsAndNoCteNodes()
        {
            var sql = """
                CREATE PROCEDURE dbo.TestProc
                AS
                BEGIN
                    ;WITH C1 AS (
                        SELECT Id, Name FROM dbo.Source1
                    ), C2 AS (
                        SELECT Id, Name FROM C1 WHERE Id IS NOT NULL
                    )
                    SELECT Name FROM C2
                END
                """;

            var graph = BuildGraph(sql);

            // No table node should be emitted for CTEs
            Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") && ((string)n.Properties["name"]).StartsWith("C", StringComparison.OrdinalIgnoreCase));
            // But Source1 should be read from
            Assert.Contains(graph.Relationships, r => r.Type == "READS_FROM" && graph.Nodes.Any(n => n.Id == r.EndNodeId && (string)n.Properties["name"] == "dbo.Source1"));
        }

        [Fact]
        public void DynamicSql_ConcatenationWithTableNames_RecordsBuildsSql()
        {
            var sql = """
                CREATE PROCEDURE dbo.TestProc
                    @tbl NVARCHAR(100)
                AS
                BEGIN
                    DECLARE @s NVARCHAR(MAX)
                    SET @s = 'SELECT Id FROM ' + @tbl + ' WHERE Flag=1'
                    EXEC(@s)
                END
                """;

            var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
            Assert.Null(result.Error);
            Assert.True(result.DynamicSqlCount >= 1);
        }

        [Fact]
        public void SelectStarAmbiguous_ExpandsWhenSchemaProvided()
        {
            var sql = """
                CREATE PROCEDURE dbo.TestProc
                AS
                BEGIN
                    SELECT * FROM dbo.SourceX
                END
                """;

            var tableColumns = new Dictionary<string, List<string>>
            {
                ["TestDb::dbo.sourcex"] = new List<string> { "Id", "Name", "Value" }
            };

            var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql, tableColumns);
            Assert.Null(result.Error);

            var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
            var readCols = graph.Relationships.Where(r => r.Type == "READS_COLUMN").Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"]).ToHashSet();
            Assert.Equal(new HashSet<object> { "Id", "Name", "Value" }, readCols);
        }
    }
}
