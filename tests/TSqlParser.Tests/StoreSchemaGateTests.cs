namespace TSqlParser.Tests;

/// <summary>
/// Ata el contrato del store a Vocab. Antes de StoreSchema, McpTools reescribía los tipos
/// de arista como literales: renombrar uno en Vocab no rompía la compilación y dejaba al
/// MCP devolviendo `affected:[]` en silencio, que es indistinguible de "nada depende de
/// esto". Estos gates convierten ese fallo mudo en rojo.
/// </summary>
public class StoreSchemaGateTests
{
    [Fact]
    public void ImpactEdgeTypes_SonUnSubconjuntoDeVocab()
    {
        var desconocidos = StoreSchema.ImpactEdgeTypes
            .Where(t => !Vocab.KnownEdgeTypes.Contains(t))
            .ToList();

        Assert.True(desconocidos.Count == 0,
            $"StoreSchema.ImpactEdgeTypes contiene tipos que Vocab.KnownEdgeTypes no declara: " +
            $"{string.Join(", ", desconocidos)}. O se renombraron en Vocab, o el subconjunto está mal.");
    }

    [Fact]
    public void ColumnRefEdgeTypes_SonUnSubconjuntoDeVocab()
    {
        var desconocidos = StoreSchema.ColumnRefEdgeTypes
            .Where(t => !Vocab.KnownEdgeTypes.Contains(t))
            .ToList();

        Assert.True(desconocidos.Count == 0,
            $"StoreSchema.ColumnRefEdgeTypes contiene tipos que Vocab.KnownEdgeTypes no declara: " +
            $"{string.Join(", ", desconocidos)}.");
    }

    [Fact]
    public void AddressableLabels_SonUnSubconjuntoDeVocab()
    {
        var desconocidos = StoreSchema.AddressableLabels
            .Where(l => !Vocab.KnownNodeLabels.Contains(l))
            .ToList();

        Assert.True(desconocidos.Count == 0,
            $"StoreSchema.AddressableLabels contiene labels que Vocab.KnownNodeLabels no declara: " +
            $"{string.Join(", ", desconocidos)}.");
    }

    [Theory]
    [InlineData("Db::dbo.Proc#step3", "Db::dbo.Proc")]
    [InlineData("Db::dbo.Proc", "Db::dbo.Proc")]
    [InlineData("Db:table:dbo.T:column:C", "Db:table:dbo.T:column:C")]
    public void RollUpStep_EnrollaSoloLosIdsDePaso(string id, string esperado) =>
        Assert.Equal(esperado, StoreSchema.RollUpStep(id));

    [Fact]
    public void FormatVersion_CoincideConLaQueEscribeElExportador()
    {
        // SqliteExporter escribe meta['format']; si divergen, un consumidor que valide la
        // versión rechaza stores válidos (o acepta incompatibles).
        var escrita = File.ReadAllText(Path.Combine(
            CorpusManifest.FindRepoRoot(AppContext.BaseDirectory)!,
            "src", "Parser.Graph", "Export", "SqliteExporter.cs"));

        Assert.Contains($"\"{StoreSchema.FormatVersion}\"", escrita);
    }
}
