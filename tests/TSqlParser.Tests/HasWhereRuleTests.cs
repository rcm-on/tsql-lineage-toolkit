using Parser.Graph;

namespace TSqlParser.Tests;

/// <summary>
/// La regla "UPDATE/DELETE sin WHERE" usaba la ausencia de aristas FILTERS_ON como sustituto
/// de la ausencia de WHERE. No son lo mismo, y fallaba en las dos direcciones: un WHERE contra
/// #temp o @tabla no emite arista (falso positivo), y un JOIN ... ON sí la emite aunque no haya
/// WHERE (falso negativo). Medido sobre DNN en docs/auditoria-dnn.md 6.1: 2 falsos positivos de
/// 4 hallazgos, más 2 sentencias sin WHERE que la regla no veía.
/// </summary>
public class HasWhereRuleTests
{
    private const string Db = "TestDb";

    private static GraphPayload Build(params (string Name, string Sql)[] fuentes)
    {
        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);
        return GraphExporter.Build(resultados, includeColumns: true);
    }

    private static bool DisparaSinWhere(GraphPayload g, string objeto) =>
        RiskAnalyzer.Analyze(g).Any(f => f.Rule == "UPDATE/DELETE sin WHERE" && f.Component == objeto);

    /// <summary>Una línea puede producir varios pasos (el DELETE y el SELECT de su
    /// subconsulta comparten line_no), así que hay que discriminar también por acción.</summary>
    private static bool HasWhereDe(GraphPayload g, string objeto, int linea, string accion = "DELETE")
    {
        var paso = g.Nodes.Single(n =>
            n.Labels.Contains("Step") &&
            n.Id.StartsWith($"{Db}::{objeto}#", StringComparison.Ordinal) &&
            Convert.ToInt32(n.Properties["line_no"]) == linea &&
            (string)n.Properties["action"] == accion);
        return (bool)paso.Properties["has_where"];
    }

    // ── el falso positivo: WHERE contra entidades no modeladas ──────────────

    [Fact]
    public void DeleteConWhereContraTablaTemporal_NoEsHallazgo()
    {
        // Patrón real de dbo.PurgeScheduleHistory: el WHERE existe, pero su único predicado
        // apunta a una tabla temporal, que el grafo no modela -> cero aristas FILTERS_ON.
        var g = Build(("dbo.PurgaPorLotes", """
            CREATE PROCEDURE dbo.PurgaPorLotes AS
            BEGIN
                CREATE TABLE #Borrar (Id int NOT NULL);
                DELETE FROM dbo.Historico WHERE Id IN (SELECT TOP (1000) Id FROM #Borrar);
            END
            """));

        Assert.True(HasWhereDe(g, "dbo.PurgaPorLotes", 4));
        Assert.False(DisparaSinWhere(g, "dbo.PurgaPorLotes"));
    }

    [Fact]
    public void DeleteConWhereContraVariableDeTabla_NoEsHallazgo()
    {
        // Patrón real de dbo.BuildTabLevelAndPath.
        var g = Build(("dbo.PurgaVariable", """
            CREATE PROCEDURE dbo.PurgaVariable AS
            BEGIN
                DECLARE @Hijos TABLE (Id int);
                DELETE FROM @Hijos WHERE Id = 1;
            END
            """));

        Assert.True(HasWhereDe(g, "dbo.PurgaVariable", 4));
        Assert.False(DisparaSinWhere(g, "dbo.PurgaVariable"));
    }

    // ── el falso negativo: FILTERS_ON que viene de un JOIN, no de un WHERE ──

    [Fact]
    public void DeleteConJoinPeroSinWhere_SiEsHallazgo()
    {
        // Patrón real de dbo.DeleteOrphanedAspNetUsers: el ON del JOIN emite FILTERS_ON, así
        // que la regla vieja lo daba por filtrado. No hay WHERE.
        var g = Build(("dbo.BorraPorJoin", """
            CREATE PROCEDURE dbo.BorraPorJoin AS
            BEGIN
                DELETE m FROM dbo.Membresia m
                INNER JOIN dbo.Huerfanos o ON m.UserId = o.UserId;
            END
            """));

        Assert.False(HasWhereDe(g, "dbo.BorraPorJoin", 3));
        Assert.True(DisparaSinWhere(g, "dbo.BorraPorJoin"));
    }

    // ── controles: el caso llano en los dos sentidos ────────────────────────

    [Fact]
    public void DeleteSinWhereDeVerdad_SiEsHallazgo()
    {
        var g = Build(("dbo.BorraTodo", "CREATE PROCEDURE dbo.BorraTodo AS BEGIN DELETE FROM dbo.Registro; END"));
        Assert.True(DisparaSinWhere(g, "dbo.BorraTodo"));
    }

    [Fact]
    public void DeleteConWhereContraColumnaReal_NoEsHallazgo()
    {
        var g = Build(("dbo.BorraUno", "CREATE PROCEDURE dbo.BorraUno @Id int AS BEGIN DELETE FROM dbo.Registro WHERE Id = @Id; END"));
        Assert.False(DisparaSinWhere(g, "dbo.BorraUno"));
    }

    [Fact]
    public void LosDosCasosEnElMismoObjeto_SoloDisparaElQueNoTieneWhere()
    {
        // Control interno tomado de dbo.DeleteEventLog, que trae los dos casos sobre la misma
        // tabla: prueba que el mecanismo discrimina dentro de un solo objeto.
        var g = Build(("dbo.BorraLog", """
            CREATE PROCEDURE dbo.BorraLog @Guid varchar(36) AS
            BEGIN
                IF @Guid is null
                    DELETE FROM dbo.Registro;
                ELSE
                    DELETE FROM dbo.Registro WHERE LogGUID = @Guid;
            END
            """));

        Assert.False(HasWhereDe(g, "dbo.BorraLog", 4));
        Assert.True(HasWhereDe(g, "dbo.BorraLog", 6));
        Assert.True(DisparaSinWhere(g, "dbo.BorraLog"));
    }
}
