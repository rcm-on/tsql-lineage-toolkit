using System.Text.Json;
using NetParser;
using Parser.Contracts;

namespace NetParser.Tests;

/// <summary>
/// Gate del puente app↔SQL: reproduce la referencia del spike
/// (app-bridge-spike/reference/expected_bridge_edges.json) sobre la copia de
/// sample-app en fixtures. 6 aristas EXTRACTED exactas (precisión 1.0) y el
/// patrón D (proc concatenado sin call-sites literales) como candidatos
/// AMBIGUOUS que incluyen los 3 esperados y nunca se promocionan.
/// </summary>
public class LiveSqlTests
{
    private static readonly Lazy<GraphPayload> Payload = new(() => Fixtures.Extract("sample-app"));

    private static List<GraphRel> Bridges =>
        Payload.Value.Relationships.Where(r => r.Type == "EXECUTES_SQL").ToList();

    public static IEnumerable<object[]> ExpectedExtracted()
    {
        foreach (var e in Fixtures.Reference().Where(e => e.Expect == "EXTRACTED"))
            yield return new object[] { e.Method, e.Target, e.Line };
    }

    [Theory]
    [MemberData(nameof(ExpectedExtracted))]
    public void Extracted_edges_match_reference(string method, string target, int line)
    {
        var edge = Bridges.SingleOrDefault(r =>
            r.EndNodeId == target &&
            r.StartNodeId.EndsWith("." + method, StringComparison.Ordinal) &&
            (string)r.Properties["confidence"] == "EXTRACTED");

        Assert.NotNull(edge);
        Assert.Equal(line, Convert.ToInt32(edge!.Properties["line"]));
    }

    [Fact]
    public void No_false_positives_among_confident_edges()
    {
        var expected = Fixtures.Reference().Where(e => e.Expect == "EXTRACTED")
            .Select(e => (e.Method, e.Target)).ToHashSet();

        foreach (var edge in Bridges.Where(r => (string)r.Properties["confidence"] is "EXTRACTED" or "RESOLVED"))
        {
            string method = edge.StartNodeId[(edge.StartNodeId.LastIndexOf('.') + 1)..];
            Assert.Contains((method, edge.EndNodeId), expected);
        }
    }

    [Fact]
    public void PatternD_yields_ambiguous_candidates_including_the_three_expected()
    {
        var ambiguous = Bridges
            .Where(r => r.StartNodeId.EndsWith(".RunIntegrationFeed", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(ambiguous);
        Assert.All(ambiguous, r => Assert.Equal("AMBIGUOUS", (string)r.Properties["confidence"]));

        var targets = ambiguous.Select(r => r.EndNodeId).ToHashSet();
        foreach (var e in Fixtures.Reference().Where(e => e.Expect == "AMBIGUOUS"))
            Assert.Contains(e.Target, targets);
    }
}

public static class Fixtures
{
    public static string Dir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static GraphPayload Extract(string fixture) =>
        new NetExtractor
        {
            CatalogPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "model.json"),
        }.Extract(Dir(fixture));

    public record ReferenceEdge(string Method, string Target, int Line, string Expect);

    public static List<ReferenceEdge> Reference()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "expected_bridge_edges.json")));
        return doc.RootElement.GetProperty("edges").EnumerateArray()
            .Select(e => new ReferenceEdge(
                e.GetProperty("app_method").GetString()!,
                e.GetProperty("target").GetString()!,
                e.GetProperty("line").GetInt32(),
                e.GetProperty("expect").GetString()!))
            .ToList();
    }
}
