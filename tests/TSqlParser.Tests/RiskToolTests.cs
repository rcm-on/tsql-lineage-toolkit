using System.Text.Json;
using Microsoft.Data.Sqlite;
using Parser.Graph;
using TSqlParser;

namespace TSqlParser.Tests;

public class RiskToolTests : IDisposable
{
    private const string Db = "TestDb";
    private readonly List<string> _temporales = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _temporales)
            if (File.Exists(f)) File.Delete(f);
    }

    private string ConstruirDb(IEnumerable<ExecutionPlanParser.ParsedPlan>? planes, params (string Name, string Sql)[] fuentes)
    {
        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        if (planes != null)
            PlanEnricher.Enrich(grafo, planes);

        var ruta = Path.Combine(Path.GetTempPath(), $"risk-tool-{Guid.NewGuid():n}.db");
        _temporales.Add(ruta);
        SqliteExporter.Write(grafo, ruta, Db, "TestProj");
        return ruta;
    }

    private static SqliteConnection Abrir(string ruta)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    private static List<Dictionary<string, object?>> Hallazgos(Dictionary<string, object?> r) =>
        (List<Dictionary<string, object?>>)r["hallazgos"]!;

    // Mismo patrón que eval/bad-practices/sql/20_usp_SearchCustomers_Injection.sql:
    // @sql se asigna EN EL MISMO SELECT desde una columna de tabla y construye el SQL
    // ejecutado - dato de tabla -> string ejecutable, la regla "Inyección SQL" (crit).
    private const string ProcInjection = @"
        CREATE PROCEDURE dbo.Injection AS
        BEGIN
            DECLARE @sql NVARCHAR(MAX);
            SELECT @sql = N'SELECT * FROM dbo.T1 WHERE Nombre = ''' + Nombre + ''''
            FROM dbo.Config
            WHERE Id = 1;
            EXEC (@sql);
        END";

    // ── contenido de hallazgos ──────────────────────────────────────────────

    [Fact]
    public void Risks_ProcConInjection_AparceConEvidenciaEstructural()
    {
        using var conn = Abrir(ConstruirDb(null, ("dbo.Injection", ProcInjection)));

        var r = RiskQueries.Risks(conn, null, 15);

        var hallazgos = Hallazgos(r);
        Assert.NotEmpty(hallazgos);
        var inyeccion = hallazgos.Single(h => (string)h["regla"]! == "Inyección SQL");
        Assert.Equal("crit", inyeccion["severidad"]);
        Assert.Equal("estructural", inyeccion["evidencia"]);
    }

    [Fact]
    public void Risks_FiltraPorSeveridadMinima_SoloCritYHigh()
    {
        // dbo.Injection dispara crit; dbo.SelectStar dispara low (SELECT *).
        var fuentes = new (string, string)[]
        {
            ("dbo.Injection", ProcInjection),
            ("dbo.SelectStar", "CREATE PROCEDURE dbo.SelectStar AS BEGIN SELECT * FROM dbo.T1; END"),
        };
        using var conn = Abrir(ConstruirDb(null, fuentes));

        var r = RiskQueries.Risks(conn, "high", 15);

        var hallazgos = Hallazgos(r);
        Assert.NotEmpty(hallazgos);
        Assert.All(hallazgos, h => Assert.True((string)h["severidad"]! is "crit" or "high"));
    }

    [Fact]
    public void Risks_SeveridadInvalida_LanzaMcpToolException()
    {
        using var conn = Abrir(ConstruirDb(null, ("dbo.Simple", "CREATE PROCEDURE dbo.Simple AS BEGIN SELECT 1; END")));

        Assert.Throws<McpToolException>(() => RiskQueries.Risks(conn, "bogus", 15));
    }

    [Fact]
    public void Risks_SinHallazgos_DevuelveReason()
    {
        using var conn = Abrir(ConstruirDb(null, ("dbo.Trivial", "CREATE PROCEDURE dbo.Trivial AS BEGIN SELECT 1; END")));

        var r = RiskQueries.Risks(conn, null, 15);

        Assert.True(r.ContainsKey("reason"));
        Assert.DoesNotContain(r, kv => kv.Key == "hallazgos");
    }

    // ── datos_de_ejecucion ──────────────────────────────────────────────────

    [Fact]
    public void Risks_SinPlanEnricher_DatosDeEjecucionFalse_ConAdvertencia()
    {
        using var conn = Abrir(ConstruirDb(null, ("dbo.Injection", ProcInjection)));

        var r = RiskQueries.Risks(conn, null, 15);

        Assert.False((bool)r["datos_de_ejecucion"]!);
        Assert.True(r.ContainsKey("advertencia"));
        Assert.Contains("orden estructural", (string)r["advertencia"]!);
    }

    private const string PlanXml =
        """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
        <StmtProc><Procedure Schema="[dbo]" ProcName="[ConLectura]"/><Statements>
        <StmtSimple StatementType="SELECT" StatementText="SELECT * FROM T1"><QueryPlan><RelOp PhysicalOp="Table Scan"><Object Database="[TestDb]" Schema="[dbo]" Table="[T1]"/></RelOp></QueryPlan></StmtSimple>
        </Statements></StmtProc>
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    [Fact]
    public void Risks_ConPlanEnricher_DatosDeEjecucionTrue_SinAdvertencia()
    {
        var plan = ExecutionPlanParser.ParseXml(PlanXml);
        using var conn = Abrir(ConstruirDb(new[] { plan },
            ("dbo.ConLectura", "CREATE PROCEDURE dbo.ConLectura AS BEGIN SELECT * FROM dbo.T1; END")));

        var r = RiskQueries.Risks(conn, null, 15);

        Assert.True((bool)r["datos_de_ejecucion"]!);
        Assert.False(r.ContainsKey("advertencia"));
    }

    // ── presupuesto de respuesta ──────────────────────────────────────────

    [Fact]
    public void Risks_CabeEnElPresupuesto_ConCorpusRealDeMalasPracticas()
    {
        var root = RepoRoot();
        var sqlDir = Path.Combine(root, "eval", "bad-practices", "sql");
        var sqlFiles = Directory.GetFiles(sqlDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(sqlFiles);

        var dir = Path.Combine(Path.GetTempPath(), "risk-tool-corpus-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var inputPath = Path.Combine(dir, "input.json");
            Assert.Equal(0, SqlFileLoader.Run("BadPracticesDB", inputPath, sqlFiles));
            var (resultados, tableSchemas) = InputAnalyzer.Analyze(inputPath);
            var grafo = GraphExporter.Build(resultados, includeColumns: true, tableSchemas);

            var ruta = Path.Combine(dir, "risks.db");
            SqliteExporter.Write(grafo, ruta, "BadPracticesDB", "TestProj");
            Dictionary<string, object?> r;
            using (var conn = Abrir(ruta))
                r = RiskQueries.Risks(conn, null, 15);
            SqliteConnection.ClearAllPools();

            var bytes = JsonSerializer.SerializeToUtf8Bytes(r);

            Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
                $"risks ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
            Assert.True((int)r["total"]! > 15, "el corpus de malas prácticas debería superar el límite por defecto de 15");
            Assert.True((bool?)r["truncated"] ?? false, "con más hallazgos que el techo, truncated debe ser true");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "bad-practices", "expected-findings.json")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontró eval/bad-practices/expected-findings.json subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }
}
