using System.Text.Json;
using TSqlParser;
using Xunit;

namespace TSqlParser.Tests.ChatGpt
{
    public class LineageSpecRunnerTests
    {
        private const string Db = "TestDb";

        private record DeriveSpec(string targetTable, string targetColumn, string sourceTable, string sourceColumn);
        private record Spec(string id, string name, string sql, List<DeriveSpec>? expectedDerives);

        private static GraphPayload BuildGraphFromSql(string sql)
        {
            var res = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
            Assert.NotNull(res);
            return GraphExporter.Build(new List<ObjectResult> { res }, includeColumns: true);
        }

        [Fact]
        public void RunAllSpecs_ValidateExpectedDerivations()
        {
            var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lineage_specs"));
            if (!Directory.Exists(baseDir)) Assert.Fail($"Specs directory not found: {baseDir}");

            foreach (var f in Directory.EnumerateFiles(baseDir, "*.json").OrderBy(x => x))
            {
                var json = File.ReadAllText(f);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                var id = doc.GetProperty("id").GetString() ?? Path.GetFileName(f);
                var name = doc.GetProperty("name").GetString() ?? id;
                var sql = doc.GetProperty("sql").GetString() ?? string.Empty;

                var expected = new List<DeriveSpec>();
                if (doc.TryGetProperty("expectedDerives", out var ed) && ed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ed.EnumerateArray())
                    {
                        var tt = item.GetProperty("targetTable").GetString()!;
                        var tc = item.GetProperty("targetColumn").GetString()!;
                        var st = item.GetProperty("sourceTable").GetString()!;
                        var sc = item.GetProperty("sourceColumn").GetString()!;
                        expected.Add(new DeriveSpec(tt, tc, st, sc));
                    }
                }

                var graph = BuildGraphFromSql(sql);

                // Build quick lookup for column nodes
                GraphNode? FindCol(string table, string col)
                {
                    return graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Column")
                        && string.Equals(((string?)n.Properties["table"]) ?? string.Empty, table, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(((string?)n.Properties["name"]) ?? string.Empty, col, StringComparison.OrdinalIgnoreCase));
                }

                var derives = graph.Relationships.Where(r => r.Type == "DERIVES_FROM").ToList();

                if (expected.Count == 0)
                {
                    // If spec expects no derivations, assert none
                    Assert.DoesNotContain(derives, _ => true);
                    continue;
                }

                foreach (var e in expected)
                {
                    var tnode = FindCol(e.targetTable, e.targetColumn);
                    var snode = FindCol(e.sourceTable, e.sourceColumn);
                    Assert.NotNull(tnode);
                    Assert.NotNull(snode);
                    var found = derives.Any(d => d.StartNodeId == tnode!.Id && d.EndNodeId == snode!.Id);
                    Assert.True(found, $"Missing derivation in spec {id}/{name}: {e.targetTable}.{e.targetColumn} <- {e.sourceTable}.{e.sourceColumn}");
                }
            }
        }
    }
}
