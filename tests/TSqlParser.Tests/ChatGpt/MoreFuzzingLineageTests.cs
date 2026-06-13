using System;
using System.Collections.Generic;
using System.Linq;
using TSqlParser;

namespace TSqlParser.Tests.ChatGpt
{
    public class MoreFuzzingLineageTests
    {
        private const string Db = "TestDb";

        private static ObjectResult Analyze(string sql)
        {
            return SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        }

        [Fact]
        public void DeterministicFuzz_NoCrash()
        {
            var seed = 123456;
            var rnd = new Random(seed);

            var fragments = new[]
            {
                "SELECT 1",
                "SELECT * FROM dbo.A",
                "SELECT A.Col1, B.Col2 FROM dbo.A A JOIN dbo.B B ON A.Id=B.Id",
                "INSERT INTO dbo.T(Col) SELECT Col FROM dbo.S",
                "UPDATE dbo.T SET Col = (SELECT MAX(Val) FROM dbo.S)",
                "DELETE FROM dbo.T WHERE EXISTS(SELECT 1 FROM dbo.S WHERE S.Id = T.Id)",
                "SELECT Col1 FROM dbo.S1 UNION ALL SELECT Col1 FROM dbo.S2",
                ";WITH CTE AS (SELECT Id FROM dbo.S) SELECT * FROM CTE",
                "EXEC('SELECT 1')",
                "SELECT TOP 10 * FROM dbo.A ORDER BY Id",
                "SELECT Col FROM (SELECT Col FROM dbo.S) d",
                "INSERT INTO dbo.T SELECT A.x + B.y FROM dbo.A A CROSS JOIN dbo.B B",
                "SELECT CASE WHEN x IS NULL THEN 0 ELSE x END FROM dbo.S",
                "SELECT COALESCE(a.x, b.y) FROM dbo.A a JOIN dbo.B b ON a.Id=b.Id",
            };

            const int iterations = 200;
            for (int i = 0; i < iterations; i++)
            {
                var count = rnd.Next(1, 4);
                var parts = new List<string>();
                for (int k = 0; k < count; k++) parts.Add(fragments[rnd.Next(fragments.Length)]);
                var body = string.Join("; ", parts);
                var sql = $"CREATE PROCEDURE dbo.TestProc AS BEGIN {body}; END";

                ObjectResult res = null;
                try
                {
                    res = Analyze(sql);
                }
                catch (Exception ex)
                {
                    Assert.True(false, $"Analyzer threw an exception on SQL:\n{sql}\n{ex}");
                }

                Assert.NotNull(res);
            }
        }

        [Fact]
        public void FuzzGraphBuild_Subset_NoCrash()
        {
            var variants = new List<string>
            {
                "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target(Col1) SELECT Col1 FROM dbo.S1; END",
                "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target(Col1) SELECT Col1 FROM dbo.S1 UNION ALL SELECT Col1 FROM dbo.S2; END",
                "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target SELECT A.Col + B.Col FROM dbo.A A JOIN dbo.B B ON A.Id=B.Id; END",
                "CREATE PROCEDURE dbo.TestProc AS BEGIN ;WITH C AS (SELECT Id,Name FROM dbo.S) INSERT INTO dbo.T(Name) SELECT Name FROM C; END",
                "CREATE PROCEDURE dbo.TestProc AS BEGIN DECLARE @s NVARCHAR(MAX); SET @s = 'SELECT * FROM ' + 'dbo.S'; EXEC(@s); END"
            };

            foreach (var sql in variants)
            {
                ObjectResult res = null;
                try { res = Analyze(sql); } catch (Exception ex) { Assert.True(false, $"Analyzer threw: {ex}\nSQL: {sql}"); }
                Assert.NotNull(res);

                GraphPayload g = null;
                try { g = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: true); } catch (Exception ex) { Assert.True(false, $"GraphExporter threw: {ex}\nSQL: {sql}"); }
                Assert.NotNull(g);
            }
        }

        [Fact]
        public void TargetedLineageChecks_SimpleCases()
        {
            // Simple insert-from-select should produce DERIVES_FROM edges
            var sql = "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target(Col1) SELECT Col1 FROM dbo.Source; END";
            var res = Analyze(sql);
            Assert.NotNull(res);
            var graph = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: true);
            Assert.Contains(graph.Relationships, r => r.Type == "DERIVES_FROM");
        }
    }
}
