using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate de <see cref="ModuleCoverage"/>: la reconciliación módulo a módulo que <c>recall</c> no
/// hace. <c>recall</c> mide referencias de columna, así que un módulo que no se extrae, que no
/// parsea o que no aporta ninguna columna no aparece en su medida ni para bien ni para mal.
///
/// Lo que se gatea aquí es el veredicto, que es donde está la trampa: un módulo con DML en el
/// cuerpo y cero aristas tiene que salir como DEFECTO, no como "limpio".
/// </summary>
public class ModuleCoverageTests
{
    [Fact]
    public void ExtraidoConDmlYSinAristas_EsDefecto()
    {
        var (estado, detalle) = ModuleCoverage.Clasificar(
            definicion: "ok", extraido: true, error: null, lineageEdges: 0,
            cuerpo: "CREATE PROCEDURE dbo.p1 AS BEGIN INSERT INTO dbo.t1 (c1) VALUES (1); END");

        Assert.Equal(ModuleCoverage.SinAristasConDml, estado);
        Assert.NotEqual("", detalle);
    }

    [Fact]
    public void ExtraidoSinDmlYSinAristas_NoSeAcusaDeDefecto()
    {
        var (estado, _) = ModuleCoverage.Clasificar(
            definicion: "ok", extraido: true, error: null, lineageEdges: 0,
            cuerpo: "CREATE PROCEDURE dbo.p1 AS BEGIN PRINT 'a'; END");

        Assert.Equal(ModuleCoverage.SinAristasSinDml, estado);
    }

    [Fact]
    public void EnElCatalogoYNoEnElInput_EsNoExtraido()
    {
        var (estado, _) = ModuleCoverage.Clasificar(
            definicion: "ok", extraido: false, error: null, lineageEdges: 0, cuerpo: "");

        Assert.Equal(ModuleCoverage.NoExtraido, estado);
    }

    [Fact]
    public void CifradoOSinDefinicion_NoEsDefectoPeroConsta()
    {
        // Un cifrado que orquesta media base es cobertura perdida aunque ninguna herramienta
        // pueda leerlo: no se cuenta como defecto, pero no puede desaparecer del recuento.
        Assert.Equal(ModuleCoverage.NoParseablePorDiseno,
            ModuleCoverage.Clasificar("cifrado", extraido: false, error: null, lineageEdges: 0, cuerpo: "").Estado);
        Assert.Equal(ModuleCoverage.NoParseablePorDiseno,
            ModuleCoverage.Clasificar("sin_definicion", extraido: false, error: null, lineageEdges: 0, cuerpo: "").Estado);
    }

    [Fact]
    public void ErrorDeParseo_SeNombraYSeRecorta()
    {
        var largo = new string('x', 500);
        var (estado, detalle) = ModuleCoverage.Clasificar(
            definicion: "ok", extraido: true, error: largo, lineageEdges: 0, cuerpo: "");

        Assert.Equal(ModuleCoverage.ErrorParseo, estado);
        Assert.Equal(200, detalle.Length);
    }

    [Fact]
    public void ConAristas_EsOk()
    {
        Assert.Equal(ModuleCoverage.Ok,
            ModuleCoverage.Clasificar("ok", extraido: true, error: null, lineageEdges: 7, cuerpo: "").Estado);
    }

    [Fact]
    public void ContarAristas_AtribuyeElStepASuObjetoPropietario()
    {
        var results = new[]
        {
            SqlAnalyzer.AnalyzeObject("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    INSERT INTO dbo.t2 (c1) SELECT c1 FROM dbo.t1;
END"),
        }.ToList();

        var graph = GraphExporter.Build(results, includeColumns: true, new List<TableSchemaResult>());
        var cuenta = ModuleCoverage.ContarAristasPorModulo(graph);

        Assert.True(cuenta.TryGetValue("dbo.p1", out var n) && n > 0,
            "Las aristas del step tienen que atribuirse al procedimiento propietario, no al step. Claves: " +
            string.Join(", ", cuenta.Keys));
    }
}
