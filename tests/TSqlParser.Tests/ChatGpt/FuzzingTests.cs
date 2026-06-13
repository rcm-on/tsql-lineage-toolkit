using TSqlParser;

namespace TSqlParser.Tests.ChatGpt
{
    public class FuzzingTests
    {
        private const string Db = "TestDb";

        private static ObjectResult Analyze(string sql)
        {
            var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
            return result;
        }

        [Fact]
        public void QuickFuzz_ManyCombinations_DoNotCrash()
        {
            var fragments = new[]
            {
                "SELECT 1",
                "SELECT * FROM dbo.A",
                "INSERT INTO dbo.T(x) SELECT y FROM dbo.S",
                "UPDATE dbo.T SET x = (SELECT MAX(v) FROM dbo.S)",
                "DELETE FROM dbo.T WHERE EXISTS(SELECT 1 FROM dbo.S WHERE S.Id = T.Id)",
                "SELECT a.Col, b.Col FROM dbo.A a JOIN dbo.B b ON a.Id=b.Id",
                "SELECT Col1 FROM dbo.S1 UNION ALL SELECT Col1 FROM dbo.S2",
                ";WITH C AS (SELECT 1) SELECT * FROM C",
                "EXEC('SELECT 1')",
            };

            // Cross-combine a few fragments to create eccentric SQL bodies.
            for (int i = 0; i < fragments.Length; i++)
            for (int j = 0; j < fragments.Length; j++)
            {
                var sql = $"CREATE PROCEDURE dbo.TestProc AS BEGIN {fragments[i]}; {fragments[j]}; END";
                var res = Analyze(sql);
                // Analyzer should not throw and should return a result (may have Error set)
                Assert.NotNull(res);
            }
        }
    }
}
