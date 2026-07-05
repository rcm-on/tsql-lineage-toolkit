using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using TSqlParser;

namespace TSqlParser.Tests.ChatGpt
{
    public class FirstResponderKitTortureTests
    {
        private const string Db = "TestDb";

        private static IEnumerable<KeyValuePair<string,string>> Mutations(string sql)
        {
            yield return new KeyValuePair<string,string>("original", sql);

            // dynamic wrapper (escape single quotes)
            var escaped = sql.Replace("'", "''");
            var wrap = $"DECLARE @s NVARCHAR(MAX); SET @s = N'{escaped}'; EXEC sp_executesql @s;";
            yield return new KeyValuePair<string,string>("dynamic_wrap", wrap);

            // append a UNION ALL to force BinaryQueryExpression handling
            var union = sql + "\n\nSELECT 1 AS union_col UNION ALL SELECT 2 AS union_col;";
            yield return new KeyValuePair<string,string>("append_union", union);

            // nested derived selection to create derived-table nesting
            var nested = sql + "\n\nSELECT d.c FROM (SELECT x as c FROM (SELECT 1 as x) innerx) d;";
            yield return new KeyValuePair<string,string>("nested_derived", nested);

            // simple obfuscated dynamic SQL
            var obf = "DECLARE @s NVARCHAR(MAX); SET @s = N''SELECT '' + N''1''; EXEC(@s);";
            yield return new KeyValuePair<string,string>("obfuscated_dynamic", obf);
        }

        [Fact]
        public void Torture_FRK_Mutations_GenerateReports()
        {
            // Locate first-responder-kit folder up the directory tree
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
                // No FRK checkout found - skip the test harmlessly
                return;
            }

            var sqlFiles = Directory.EnumerateFiles(frk, "*.sql", SearchOption.AllDirectories)
                .OrderBy(x => x)
                .Take(10)
                .ToList();

            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "frk_torture_output");
            Directory.CreateDirectory(outDir);

            foreach (var f in sqlFiles)
            {
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                foreach (var m in Mutations(text))
                {
                    ObjectResult res = null;
                    try { res = SqlAnalyzer.AnalyzeObject($"{Db}::{Path.GetFileName(f)}::{m.Key}", m.Value); }
                    catch (Exception ex) { Assert.Fail($"Analyzer threw on {f} mutation {m.Key}: {ex}"); }
                    Assert.NotNull(res);

                    GraphPayload g = null;
                    try { g = GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: true); }
                    catch (Exception ex) { Assert.Fail($"GraphExporter threw on {f} mutation {m.Key}: {ex}"); }
                    Assert.NotNull(g);

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var baseName = Path.GetFileNameWithoutExtension(f) + "_" + m.Key;
                    File.WriteAllText(Path.Combine(outDir, baseName + "_object.json"), JsonSerializer.Serialize(res, options));
                    File.WriteAllText(Path.Combine(outDir, baseName + "_graph.json"), JsonSerializer.Serialize(g, options));

                    var derives = g.Relationships.Count(r => r.Type == "DERIVES_FROM");
                    var reads = g.Relationships.Count(r => r.Type == "READS_FROM");
                    var writes = g.Relationships.Count(r => r.Type == "WRITES_TO");
                    Console.WriteLine($"{baseName}: DERIVES={derives}, READS={reads}, WRITES={writes}");
                }
            }
        }
    }
}
