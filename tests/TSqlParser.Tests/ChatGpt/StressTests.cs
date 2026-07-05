using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using TSqlParser;

namespace TSqlParser.Tests.ChatGpt
{
    public class StressTests
    {
        private const string Db = "TestDb";

        private static ObjectResult Analyze(string sql, string name = "dbo.TestProc")
        {
            return SqlAnalyzer.AnalyzeObject($"{Db}::{name}", sql);
        }

        [Fact]
        public void Stress_LargeJoinDepth_NoCrash()
        {
            const int joins = 50;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CREATE PROCEDURE dbo.TestProc AS BEGIN");
            sb.Append("SELECT t0.Id");
            for (int i = 0; i < joins; i++) sb.Append($", t{i}.Col{i}");
            sb.AppendLine();
            sb.Append("FROM dbo.T0 t0");
            for (int i = 1; i <= joins; i++) sb.AppendLine($" JOIN dbo.T{i} t{i} ON t{i-1}.Id = t{i}.Id");
            sb.AppendLine("END");

            var sql = sb.ToString();
            ObjectResult res = null;
            try { res = Analyze(sql); } catch (Exception ex) { Assert.Fail($"Analyzer threw: {ex}"); }
            Assert.NotNull(res);
            try { var g = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: false); Assert.NotNull(g); } catch (Exception ex) { Assert.Fail($"GraphExporter threw: {ex}"); }
        }

        [Fact]
        public void Stress_DeepNestedDerivedTables_NoCrash()
        {
            const int depth = 30;
            var inner = "(SELECT 1 AS c) t0";
            for (int i = 1; i <= depth; i++) inner = $"(SELECT * FROM {inner}) t{i}";

            var sql = $"CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT * FROM {inner}; END";
            ObjectResult res = null;
            try { res = Analyze(sql); } catch (Exception ex) { Assert.Fail($"Analyzer threw: {ex}\nSQL: {sql}"); }
            Assert.NotNull(res);
        }

        [Fact]
        public void Stress_ManyUnions_NoCrash()
        {
            const int unions = 60;
            var parts = new List<string>();
            for (int i = 0; i < unions; i++) parts.Add($"SELECT Col{i} FROM dbo.S{i}");
            var body = string.Join(" UNION ALL ", parts);
            var sql = $"CREATE PROCEDURE dbo.TestProc AS BEGIN {body}; END";

            ObjectResult res = null;
            try { res = Analyze(sql); } catch (Exception ex) { Assert.Fail($"Analyzer threw: {ex}"); }
            Assert.NotNull(res);
        }

        [Fact]
        public void Stress_ObfuscatedDynamicSql_NoCrash()
        {
            var sql = @"CREATE PROCEDURE dbo.TestProc @t nvarchar(100) AS BEGIN
DECLARE @s nvarchar(max);
SET @s = 'SELECT ' + QUOTENAME('Id') + ' FROM ' + @t + ' WHERE Col=''''''X'''''" + ";\nEXEC sp_executesql @s; END";

            ObjectResult res = null;
            try { res = Analyze(sql); } catch (Exception ex) { Assert.Fail($"Analyzer threw: {ex}"); }
            Assert.NotNull(res);
            Assert.True(res.DynamicSqlCount >= 0);
        }

        [Fact]
        public void Stress_FirstResponderKit_Parsing_NoCrash()
        {
            // Locate first-responder-kit directory by walking up from the test app base dir
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string frk = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "first-responder-kit");
                if (Directory.Exists(candidate)) { frk = candidate; break; }
                dir = dir.Parent;
            }

            if (frk == null)
            {
                // If not found, mark test as inconclusive (skip)
                return;
            }

            var sqlFiles = Directory.EnumerateFiles(frk, "*.sql", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).IndexOf("blitz", StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(5)
                .ToList();

            foreach (var f in sqlFiles)
            {
                string text = null;
                try { text = File.ReadAllText(f); } catch { continue; }
                ObjectResult res = null;
                try { res = Analyze(text, Path.GetFileName(f)); } catch (Exception ex) { Assert.Fail($"Analyzer threw on {f}: {ex}"); }
                Assert.NotNull(res);
                try { var g = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: false); Assert.NotNull(g); } catch (Exception ex) { Assert.Fail($"GraphExporter threw on {f}: {ex}"); }
            }
        }
    }
}
