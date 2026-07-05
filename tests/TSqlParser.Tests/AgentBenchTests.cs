using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Covers <see cref="AgentBench"/> (bench-make / bench-grade): the store-agnostic
/// agent benchmark. Self-test contract: on a store whose corpus exercises every
/// case type, bench-make emits the six cases un-skipped, the generated expected
/// answers grade as 6/6 PASS (exit 0), and a tampered answer flips to exit 2 -
/// so the harness itself can never silently pass a wrong model output.
/// </summary>
public class AgentBenchTests : IDisposable
{
    private const string Db = "BenchDb";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Mirrors eval/agent-bench/sql: a chain with a conditional hop (ProcessDay ->
    // RecalcTotals -> WriteAudit), a view with column lineage, and a written table
    // with a reader - every case type C1-C6 gets a subject. Table DDL is deliberately
    // absent: in this in-process path CREATE TABLE would become a SqlObject (the CLI
    // routes it separately as a table schema), which starves C1/C4 of subjects.
    private static readonly (string Name, string Sql)[] Corpus =
    {
        ("dbo.vCustomerOrders", "CREATE VIEW dbo.vCustomerOrders AS SELECT c.Name AS CustomerName, o.Total AS OrderTotal FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.Id;"),
        ("dbo.WriteAudit", "CREATE PROCEDURE dbo.WriteAudit @msg NVARCHAR(200) AS BEGIN INSERT INTO dbo.AuditLog (Msg) VALUES (@msg); END"),
        ("dbo.RecalcTotals", "CREATE PROCEDURE dbo.RecalcTotals AS BEGIN UPDATE o SET Total = v.OrderTotal FROM dbo.Orders o JOIN dbo.vCustomerOrders v ON v.CustomerName IS NOT NULL; IF EXISTS (SELECT 1 FROM dbo.AuditLog) EXEC dbo.WriteAudit @msg = N'recalc'; END"),
        ("dbo.ProcessDay", "CREATE PROCEDURE dbo.ProcessDay @full BIT AS BEGIN IF @full = 1 EXEC dbo.RecalcTotals; EXEC dbo.WriteAudit @msg = N'day'; END"),
        ("dbo.DailyReport", "CREATE PROCEDURE dbo.DailyReport AS BEGIN SELECT Id, Msg FROM dbo.AuditLog; END"),
    };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agent-bench-tests", Guid.NewGuid().ToString("n"));
        _tempDirs.Add(dir);
        return dir;
    }

    private (string BenchDir, string StoreDir) MakeBench()
    {
        var results = Corpus.Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Name}", o.Sql)).ToList();
        foreach (var r in results)
            Assert.Null(r.Error);
        var graph = GraphExporter.Build(results, includeColumns: true);
        var store = NewTempDir();
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);
        var bench = NewTempDir();
        Assert.Equal(0, AgentBench.Make(store, bench));
        return (bench, store);
    }

    [Fact]
    public void Make_EmitsAllSixCasesUnskipped_AndExpectedGradesPerfect()
    {
        var (bench, _) = MakeBench();

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(bench, "cases.json"))).RootElement;
        Assert.Equal(0, root.GetProperty("seed").GetInt32());
        var cases = root.GetProperty("cases");
        Assert.Equal(6, cases.GetArrayLength());
        Assert.All(cases.EnumerateArray(), c =>
            Assert.False(c.GetProperty("skipped").GetBoolean(),
                $"{c.GetProperty("id").GetString()} unexpectedly skipped"));

        // Prompts are self-contained and point the agent at the howto contract.
        var prompt = File.ReadAllText(Path.Combine(bench, "cases", "C2.prompt.md"));
        Assert.Contains("index.json", prompt);
        Assert.Contains("STRICT JSON", prompt);

        // Self-test: the generated expected answers must grade 6/6 (exit 0).
        var answers = Path.Combine(bench, "answers", "self");
        Directory.CreateDirectory(answers);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(bench, "expected")))
            File.Copy(f, Path.Combine(answers, Path.GetFileName(f)));
        Assert.Equal(0, AgentBench.Grade(bench, answers));

        // Scorecard persisted for the comparison table.
        Assert.True(File.Exists(Path.Combine(bench, "results", "self.json")));
    }

    [Fact]
    public void Grade_FlagsWrongAndMissingAnswers()
    {
        var (bench, _) = MakeBench();
        var answers = Path.Combine(bench, "answers", "tampered");
        Directory.CreateDirectory(answers);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(bench, "expected")))
            File.Copy(f, Path.Combine(answers, Path.GetFileName(f)));

        // Wrong content on C1 (a table the object does not write) -> FAIL -> exit 2.
        File.WriteAllText(Path.Combine(answers, "C1.json"), "{\"tables\": [\"dbo.Nowhere\"]}");
        Assert.Equal(2, AgentBench.Grade(bench, answers));

        // Missing answer file also fails the run.
        File.Delete(Path.Combine(answers, "C1.json"));
        Assert.Equal(2, AgentBench.Grade(bench, answers));

        var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(bench, "results", "tampered.json"))).RootElement;
        Assert.Equal(5, score.GetProperty("pass").GetInt32());
        Assert.Equal("MISSING", score.GetProperty("per_case").GetProperty("C1").GetString());
    }

    [Fact]
    public void Make_SeedRotatesSubjectsReproducibly()
    {
        var results = Corpus.Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Name}", o.Sql)).ToList();
        var graph = GraphExporter.Build(results, includeColumns: true);
        var store = NewTempDir();
        NodeStoreExporter.Write(graph, store, Db, JsonOptions);

        string QuestionC1(string bench) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(bench, "cases.json"))).RootElement
                .GetProperty("cases").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == "C1")
                .GetProperty("question").GetString()!;

        var bench0 = NewTempDir();
        var bench0b = NewTempDir();
        var bench1 = NewTempDir();
        Assert.Equal(0, AgentBench.Make(store, bench0, seed: 0));
        Assert.Equal(0, AgentBench.Make(store, bench0b, seed: 0));
        Assert.Equal(0, AgentBench.Make(store, bench1, seed: 1));

        // Same seed => byte-identical question (fair comparison across models);
        // different seed => a different eligible subject (both writers qualify here).
        Assert.Equal(QuestionC1(bench0), QuestionC1(bench0b));
        Assert.NotEqual(QuestionC1(bench0), QuestionC1(bench1));
    }

    [Fact]
    public void Grade_TolleratesFencedAndBomAnswers()
    {
        var (bench, _) = MakeBench();
        var answers = Path.Combine(bench, "answers", "fenced");
        Directory.CreateDirectory(answers);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(bench, "expected")))
        {
            // Re-wrap every expected answer the way chat models tend to emit them.
            var body = File.ReadAllText(f);
            File.WriteAllText(Path.Combine(answers, Path.GetFileName(f)), "﻿```json\n" + body + "\n```");
        }
        Assert.Equal(0, AgentBench.Grade(bench, answers));
    }
}
