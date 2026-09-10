using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Dos huecos que no daban error ni referencia ciega, encontrados por <c>syntax-coverage</c>:
/// <c>VariableTableReference</c> (40 apariciones en 8 módulos de DNN, 22 en 3 de WWI) y
/// <c>JoinParenthesisTableReference</c>. Ninguno de los dos tenía caso en CollectTableRefsInto,
/// así que la referencia se descartaba en silencio y con ella toda columna calificada por su alias.
/// </summary>
public class TableVariableLineageTests
{
    private static GraphPayload Grafo(string sql)
    {
        var res = new List<ObjectResult> { SqlAnalyzer.AnalyzeObject("db::dbo.p1", sql) };
        Assert.Null(res[0].Error);
        return GraphExporter.Build(res, includeColumns: true, new List<TableSchemaResult>());
    }

    private static List<string> Destinos(GraphPayload g, string tipo) =>
        g.Relationships.Where(r => r.Type == tipo).Select(r => r.EndNodeId).ToList();

    [Fact]
    public void VariableDeTablaEnElFrom_YaNoProduceUnaColumnaFantasma()
    {
        // Medido antes del arreglo: con @tv sin registrar, la única tabla "real" en el ámbito
        // era dbo.t2, así que el atajo de tabla única atribuía la "c1" no calificada a
        // dbo.t2.c1 - una columna que esta consulta NO lee. No era lineage perdido: era
        // lineage inventado, que es peor, porque un grafo verosímil y falso no se nota.
        var g = Grafo(@"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    DECLARE @tv TABLE (c1 int);
    SELECT c1 FROM @tv v JOIN dbo.t2 x ON x.c9 = 1;
END");

        Assert.DoesNotContain(Destinos(g, "READS_COLUMN"), t => t.EndsWith("dbo.t2:column:c1"));
    }

    [Fact]
    public void VariableDeTabla_NoEmiteNodoDeTabla()
    {
        // @tv se registra como origen de filas, pero sigue sin ser una :Table: el grafo de
        // tablas solo contiene objetos persistidos (GraphExporter.IsTempOrVariable). Lo que
        // se gana es que el alias resuelva; lo que NO se gana todavía es la columna, porque
        // el puente al origen real (INSERT INTO @tv SELECT ... FROM t1) solo existe para la
        // escritura. Ese es el siguiente hueco, y queda dicho aquí en vez de aparentar verde.
        var g = Grafo(@"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    DECLARE @tv TABLE (c1 int);
    INSERT INTO @tv (c1) SELECT c1 FROM dbo.t1;
    SELECT v.c1 FROM @tv v;
END");

        Assert.DoesNotContain(g.Nodes, n => n.Id.Contains("@tv") && n.Labels.Contains("Table"));
        Assert.Contains(Destinos(g, "READS_COLUMN"), t => t.EndsWith("dbo.t1:column:c1"));
    }

    [Fact]
    public void JoinEntreParentesis_NoPierdeNingunoDeLosDosLados()
    {
        var g = Grafo(@"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    SELECT a.c1, b.c2 FROM (dbo.t1 a INNER JOIN dbo.t2 b ON b.c2 = a.c1);
END");

        var lee = Destinos(g, "READS_FROM");
        Assert.Contains(lee, t => t.EndsWith("dbo.t1"));
        Assert.Contains(lee, t => t.EndsWith("dbo.t2"));
    }

    [Fact]
    public void SyntaxCoverage_YaNoLosDeclaraSinCubrir()
    {
        // El instrumento y el arreglo tienen que moverse juntos: si el caso existe pero
        // syntax-coverage sigue contándolo, uno de los dos está mintiendo.
        using var scope = SyntaxCoverage.Collect();
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    DECLARE @tv TABLE (c1 int);
    SELECT v.c1 FROM @tv v;
    SELECT a.c1 FROM (dbo.t1 a INNER JOIN dbo.t2 b ON b.c2 = a.c1);
END");

        var sinCubrir = scope.Rows("dbo.p1").Where(f => !f.Benign).Select(f => f.NodeType).ToList();
        Assert.DoesNotContain("VariableTableReference", sinCubrir);
        Assert.DoesNotContain("JoinParenthesisTableReference", sinCubrir);
    }
}
