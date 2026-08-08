using NetParser;
using Parser.Contracts;

namespace NetParser.Tests;

/// <summary>
/// Gate del límite declarado del producto: ante un proyecto fuera de alcance el
/// extractor se niega a extraer en vez de devolver un grafo parcial con pinta de
/// completo. Un impacto calculado sobre extracción incompleta es determinísticamente
/// erróneo, que es peor que no responder. Con `--allow-partial` sí extrae, pero el
/// hueco viaja dentro del grafo (`analyzed=false`), no solo en la consola.
/// </summary>
public class UnsupportedProjectTests
{
    private static string Dir => Fixtures.Dir("unsupported");

    [Fact]
    public void Extraction_is_refused_and_nothing_is_produced()
    {
        var ex = Assert.Throws<UnsupportedProjectException>(() => new NetExtractor().Extract(Dir));

        Assert.Equal(2, ex.Projects.Count);
        Assert.Contains(ex.Projects, p => p.Name == "LegacyVb" && p.Reason.Contains("VB.NET"));
        Assert.Contains(ex.Projects, p => p.Name == "DesktopApp" && p.Reason.Contains("WinForms"));
    }

    /// <summary>
    /// The message is the product documentation at the moment it matters: what was
    /// rejected, why, and what to do about it.
    /// </summary>
    [Fact]
    public void The_message_states_the_limit_and_the_way_forward()
    {
        var ex = Assert.Throws<UnsupportedProjectException>(() => new NetExtractor().Extract(Dir));

        Assert.Contains("LegacyVb", ex.Message);
        Assert.Contains("DesktopApp", ex.Message);
        Assert.Contains("C# only", ex.Message);
        Assert.Contains("WinForms", ex.Message);
        Assert.Contains("--allow-partial", ex.Message);
    }

    [Fact]
    public void Allow_partial_extracts_but_writes_the_hole_into_the_graph()
    {
        var payload = new NetExtractor { AllowPartial = true }.Extract(Dir);

        var excluded = payload.Nodes
            .Where(n => n.Properties.TryGetValue("analyzed", out var a) && a is false)
            .ToList();

        Assert.Equal(2, excluded.Count);
        Assert.All(excluded, n =>
        {
            Assert.Contains("AppProject", n.Labels);
            Assert.Equal("unsupported", (string)n.Properties["kind"]);
            Assert.False(string.IsNullOrWhiteSpace((string)n.Properties["unsupported_reason"]));
        });

        // Present in the solution tree, so a walk finds them instead of missing them.
        Assert.All(excluded, n =>
            Assert.Contains(payload.Relationships, r => r.Type == "CONTAINS" && r.EndNodeId == n.Id));
    }

    /// <summary>A supported solution must not pay for the check.</summary>
    [Fact]
    public void Supported_solutions_are_unaffected()
    {
        var payload = Fixtures.Extract("efapp");

        Assert.DoesNotContain(payload.Nodes, n =>
            n.Properties.TryGetValue("analyzed", out var a) && a is false);
    }
}
