using Parser.Graph;

namespace TSqlParser.Tests;

/// <summary>
/// "Inyección SQL" exigía columna de origen y EXEC en la MISMA variable. En DNN la ruta real
/// pasa por una intermedia (PropertyName → @PivotSql → @Sql → sp_executesql) y la regla daba 0
/// (docs/auditoria-dnn.md 6.2). ASSIGNED_FROM_VAR cierra la clausura.
/// </summary>
public class InyeccionContaminacionTests
{
    private const string Db = "TestDb";

    private static GraphPayload Build(string name, string sql)
    {
        var r = SqlAnalyzer.AnalyzeObject($"{Db}::{name}", sql);
        Assert.Null(r.Error);
        return GraphExporter.Build(new List<ObjectResult> { r }, includeColumns: true);
    }

    private static RiskFinding? Inyeccion(GraphPayload g, string objeto) =>
        RiskAnalyzer.Analyze(g).SingleOrDefault(f => f.Rule == "Inyección SQL" && f.Component == objeto);

    private const string Transitiva = @"
CREATE PROCEDURE dbo.Pivotar @PortalId INT AS
BEGIN
    DECLARE @PivotSql NVARCHAR(MAX)
    SELECT @PivotSql = COALESCE(@PivotSql + ',', '') + '[' + PropertyName + ']'
        FROM dbo.ProfilePropertyDefinition pd
        WHERE pd.PortalID = @PortalId
    DECLARE @Sql NVARCHAR(MAX)
    SELECT @Sql = 'SELECT * FROM T PIVOT (MAX(v) FOR k IN (' + @PivotSql + ')) AS p'
    EXECUTE sp_executesql @Sql, N'@PortalId INT', @PortalId
END";

    [Fact]
    public void Contaminacion_a_traves_de_variable_intermedia_es_critico()
    {
        var g = Build("dbo.Pivotar", Transitiva);
        var f = Inyeccion(g, "dbo.Pivotar");
        Assert.NotNull(f);
        Assert.Equal("crit", f!.Sev);
        Assert.Contains("@PivotSql", f.Detail);
    }

    [Fact]
    public void Arista_entre_variables_sin_autobucle()
    {
        var g = Build("dbo.Pivotar", Transitiva);
        var vv = g.Relationships.Where(r => r.Type == "ASSIGNED_FROM_VAR").ToList();
        Assert.Contains(vv, r => r.StartNodeId.EndsWith("#var:@Sql") && r.EndNodeId.EndsWith("#var:@PivotSql"));
        Assert.DoesNotContain(vv, r => r.StartNodeId == r.EndNodeId);
    }

    [Fact]
    public void Solo_parametros_no_es_critico()
    {
        var g = Build("dbo.SoloParam", @"
CREATE PROCEDURE dbo.SoloParam @Tabla SYSNAME AS
BEGIN
    DECLARE @Nombre NVARCHAR(200) = QUOTENAME(@Tabla)
    DECLARE @Sql NVARCHAR(MAX)
    SET @Sql = 'SELECT * FROM ' + @Nombre
    EXEC (@Sql)
END");
        Assert.Null(Inyeccion(g, "dbo.SoloParam"));
        Assert.Contains(RiskAnalyzer.Analyze(g), f => f.Rule == "SQL dinámico" && f.Component == "dbo.SoloParam");
    }

    [Fact]
    public void Contaminacion_directa_sigue_siendo_critico()
    {
        var g = Build("dbo.Directo", @"
CREATE PROCEDURE dbo.Directo AS
BEGIN
    DECLARE @Sql NVARCHAR(MAX)
    SELECT @Sql = Definicion FROM dbo.Plantillas WHERE Id = 1
    EXEC (@Sql)
END");
        Assert.NotNull(Inyeccion(g, "dbo.Directo"));
    }
}
