using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TSqlParser;
using Xunit;

namespace TSqlParser.Tests.ChatGpt
{
    public class CapabilitySweepTests
    {
        private const string Db = "TestDb";

        private record CaseSpec(string Id, string Name, string Category, string Sql, bool ExpectDerives = false);

        [Fact]
        public void CapabilitySweep_GenerateReport()
        {
            var specs = new List<CaseSpec>
            {
                new CaseSpec("S001", "SimpleInsertSelect", "basic", "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target(Col1) SELECT Col1 FROM dbo.Source; END", true),
                new CaseSpec("S002", "InsertSelectStar", "basic", "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target SELECT * FROM dbo.Source; END"),
                new CaseSpec("S003", "InsertUnion", "union", "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target (Col1) SELECT Col1 FROM dbo.Source1 UNION ALL SELECT Col1 FROM dbo.Source2; END"),
                new CaseSpec("S004", "DerivedTableNested", "derived", "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target (C) SELECT d.SumC FROM (SELECT A.Col1 + B.Col2 AS SumC FROM dbo.A A JOIN dbo.B B ON A.Id=B.Id) d; END", true),
                new CaseSpec("S005", "CTE_Recursive", "cte", "CREATE PROCEDURE dbo.TestProc AS BEGIN WITH CTE AS (SELECT 1 AS n UNION ALL SELECT n+1 FROM CTE WHERE n<3) SELECT * FROM CTE; END"),
                new CaseSpec("S006", "UpdateFromJoin", "update", "CREATE PROCEDURE dbo.TestProc AS BEGIN UPDATE t SET t.Col = s.Col FROM dbo.T t JOIN dbo.S s ON t.Id = s.Id; END", true),
                new CaseSpec("S007", "MergeExample", "merge", "CREATE PROCEDURE dbo.TestProc AS BEGIN MERGE dbo.Target USING dbo.Source ON Target.Id=Source.Id WHEN MATCHED THEN UPDATE SET Target.Val=Source.Val WHEN NOT MATCHED THEN INSERT (Id,Val) VALUES (Source.Id, Source.Val); END", true),
                new CaseSpec("S008", "DynamicSqlExec", "dynamic", "CREATE PROCEDURE dbo.TestProc @t nvarchar(100) AS BEGIN DECLARE @s nvarchar(max); SET @s = 'SELECT Id FROM ' + @t; EXEC(@s); END"),
                new CaseSpec("S009", "SpExecutesqlParameterized", "dynamic", "CREATE PROCEDURE dbo.TestProc @p nvarchar(100) AS BEGIN DECLARE @s nvarchar(max) = 'SELECT Id FROM dbo.Source WHERE Col=@p'; EXEC sp_executesql @s, N'@p nvarchar(100)', @p=@p; END"),
                new CaseSpec("S010", "SelectWithWindow", "window", "CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT Id, ROW_NUMBER() OVER (PARTITION BY Category ORDER BY Id) rn FROM dbo.Source; END"),
                new CaseSpec("S011", "CrossApply", "apply", "CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT a.Id, x.Col FROM dbo.A a CROSS APPLY (SELECT TOP 1 Col FROM dbo.B b WHERE b.Id=a.Id ORDER BY b.Date DESC) x; END", true),
                new CaseSpec("S012", "SelectInto", "selectinto", "CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT Id, Name INTO dbo.NewTable FROM dbo.Source; END", true),
                new CaseSpec("S013", "PivotUnpivot", "pivot", "CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT * FROM (SELECT Id, Attr, Val FROM dbo.Source) s PIVOT (MAX(Val) FOR Attr IN ([A],[B],[C])) p; END"),
                new CaseSpec("S014", "CorrelatedSubquery", "subquery", "CREATE PROCEDURE dbo.TestProc AS BEGIN SELECT s.Id, (SELECT TOP 1 Name FROM dbo.Other o WHERE o.Id = s.RefId) AS Name FROM dbo.Source s; END", true),
                new CaseSpec("S015", "TableVariable", "temp", "CREATE PROCEDURE dbo.TestProc AS BEGIN DECLARE @t TABLE(Id INT, Val INT); INSERT INTO @t SELECT Id, Val FROM dbo.Source; SELECT * FROM @t; END", true),
                new CaseSpec("S016", "TempTable", "temp", "CREATE PROCEDURE dbo.TestProc AS BEGIN CREATE TABLE #t (Id INT, Val INT); INSERT INTO #t SELECT Id, Val FROM dbo.Source; SELECT * FROM #t; DROP TABLE #t; END", true),
                new CaseSpec("S017", "ComplexExpressions", "expr", "CREATE PROCEDURE dbo.TestProc AS BEGIN INSERT INTO dbo.Target (Total) SELECT ISNULL(a.Qty,0) * COALESCE(b.Price,0) FROM dbo.A a JOIN dbo.B b ON a.Id=b.Id; END", true),
                new CaseSpec("S018", "RecursiveCteDeep", "cte", string.Join("\n", new[] { "CREATE PROCEDURE dbo.TestProc AS BEGIN", "WITH R AS (SELECT 1 AS n UNION ALL SELECT n+1 FROM R WHERE n<50) SELECT * FROM R;", "END" })),
                new CaseSpec("S019", "ManyJoins", "stress", GenerateManyJoinsSql(25)),
                new CaseSpec("S020", "ManyUnions", "stress", GenerateManyUnionsSql(25)),
            };

            var report = new List<object>();

            foreach (var s in specs)
            {
                ObjectResult res = null;
                GraphPayload g = null;
                string error = null;
                try
                {
                    res = SqlAnalyzer.AnalyzeObject($"{Db}::{s.Name}", s.Sql);
                }
                catch (Exception ex)
                {
                    error = ex.ToString();
                }

                if (res != null && error == null)
                {
                    try { g = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: true); }
                    catch (Exception ex) { error = ex.ToString(); }
                }

                var derives = g?.Relationships?.Count(r => r.Type == "DERIVES_FROM") ?? 0;
                var writesCols = g?.Relationships?.Count(r => r.Type == "WRITES_COLUMN") ?? 0;
                var readsCols = g?.Relationships?.Count(r => r.Type == "READS_COLUMN") ?? 0;

                report.Add(new {
                    id = s.Id,
                    name = s.Name,
                    category = s.Category,
                    hasError = error != null,
                    error = error,
                    dynamicSqlCount = res?.DynamicSqlCount ?? 0,
                    nodes = g?.Nodes?.Count ?? 0,
                    relationships = g?.Relationships?.Count ?? 0,
                    derives = derives,
                    writesColumns = writesCols,
                    readsColumns = readsCols,
                });
            }

            var outFile = Path.Combine(Directory.GetCurrentDirectory(), "capability_report.json");
            File.WriteAllText(outFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            // Basic sanity assertions: report generated and at least one spec produced a DERIVES_FROM
            Assert.NotEmpty(report);
            Assert.True(report.OfType<dynamic>().Any(r => (int)r.derives > 0), "No DERIVES_FROM edges produced for any spec; analyzer may lack lineage capabilities.");
        }

        private static string GenerateManyJoinsSql(int joins)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CREATE PROCEDURE dbo.TestProc AS BEGIN");
            sb.Append("SELECT t0.Id");
            for (int i = 1; i <= joins; i++) sb.Append($", t{i}.Col{i}");
            sb.AppendLine();
            sb.Append("FROM dbo.T0 t0");
            for (int i = 1; i <= joins; i++) sb.AppendLine($" JOIN dbo.T{i} t{i} ON t{i-1}.Id = t{i}.Id");
            sb.AppendLine("END");
            return sb.ToString();
        }

        private static string GenerateManyUnionsSql(int unions)
        {
            var parts = new List<string>();
            for (int i = 0; i < unions; i++) parts.Add($"SELECT Col{i} FROM dbo.S{i}");
            var body = string.Join(" UNION ALL ", parts);
            return $"CREATE PROCEDURE dbo.TestProc AS BEGIN {body}; END";
        }
    }
}
