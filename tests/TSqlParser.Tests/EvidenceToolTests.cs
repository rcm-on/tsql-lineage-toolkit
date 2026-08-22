using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

/// <summary>
/// evidence: el peldaño que faltaba entre "impact dice que este objeto toca esta tabla" y
/// "puedo comprobarlo". Corpus propio con un proc de varios pasos sobre la misma tabla, uno
/// de ellos bajo un IF, para que se vea que la respuesta separa pasos y no colapsa la arista.
/// </summary>
public class EvidenceToolTests : IDisposable
{
    private const string Db = "TestDb";
    private const string Proc = "TestDb::dbo.Mueve";
    private readonly List<string> _temporales = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _temporales)
            if (File.Exists(f)) File.Delete(f);
    }

    private string ConstruirDb()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.Mueve", """
                CREATE PROCEDURE dbo.Mueve @Id int AS
                BEGIN
                    SELECT Precio FROM dbo.Origen WHERE Id = @Id;
                    IF @Id > 0
                        INSERT INTO dbo.Destino (Total) SELECT Precio FROM dbo.Origen WHERE Id = @Id;
                END
                """),
            ("dbo.Ajeno", "CREATE PROCEDURE dbo.Ajeno AS BEGIN SELECT Nombre FROM dbo.Otra; END"),
        };

        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        var ruta = Path.Combine(Path.GetTempPath(), $"evidence-test-{Guid.NewGuid():n}.db");
        _temporales.Add(ruta);
        SqliteExporter.Write(grafo, ruta, Db, "TestProj");
        return ruta;
    }

    private SqliteConnection Abrir(string ruta)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    private static List<Dictionary<string, object?>> Pasos(Dictionary<string, object?> r) =>
        (List<Dictionary<string, object?>>)r["pasos"]!;

    // ── camino feliz ────────────────────────────────────────────────────────

    [Fact]
    public void Evidence_DevuelveUnPasoPorLecturaConLineaYAccion()
    {
        using var conn = Abrir(ConstruirDb());

        var r = EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.origen", 10);

        var pasos = Pasos(r);
        Assert.NotEmpty(pasos);
        // Dos sentencias distintas leen dbo.Origen: la arista es una, los pasos son dos.
        Assert.Equal(2, pasos.Count(p => (string)p["relacion"]! == "READS_FROM"));
        foreach (var p in pasos)
        {
            Assert.NotNull(p["line_no"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)p["accion"]));
        }
        Assert.Equal(Proc, r["object_id"]);
    }

    [Fact]
    public void Evidence_OrdenaPorLineaAscendente()
    {
        using var conn = Abrir(ConstruirDb());

        var lineas = Pasos(EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.origen", 10))
            .Select(p => (long)p["line_no"]!)
            .ToList();

        Assert.Equal(lineas.OrderBy(l => l).ToList(), lineas);
    }

    [Fact]
    public void Evidence_MarcaElPasoQueCuelgaDeUnIf()
    {
        using var conn = Abrir(ConstruirDb());

        var pasos = Pasos(EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.destino", 10));

        Assert.Contains(pasos, p => p.ContainsKey("bajo_condicion") && ((string)p["bajo_condicion"]!).Contains("IF"));
    }

    [Fact]
    public void Evidence_DeclaraSuAlcance()
    {
        using var conn = Abrir(ConstruirDb());

        var r = EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.origen", 10);

        // El campo existe para que la respuesta no aparente citar SQL que el store no guarda.
        Assert.Contains("no el SQL literal", (string)r["alcance"]!);
    }

    [Fact]
    public void Evidence_ResuelveColumnaAdemasDeTabla()
    {
        using var conn = Abrir(ConstruirDb());

        var pasos = Pasos(EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.origen:column:Precio", 10));

        Assert.NotEmpty(pasos);
        Assert.All(pasos, p => Assert.Contains("COLUMN", (string)p["relacion"]!));
    }

    // ── nombres sueltos ─────────────────────────────────────────────────────

    [Fact]
    public void Evidence_NombreSueltoUnicoSeResuelve()
    {
        using var conn = Abrir(ConstruirDb());

        var r = EvidenceQueries.Evidence(conn, Proc, "dbo.destino", 10);

        Assert.Equal($"{Db}:table:dbo.destino", r["target_id"]);
        Assert.NotEmpty(Pasos(r));
    }

    [Fact]
    public void Evidence_NombreAmbiguoDevuelveCandidatosSinElegir()
    {
        using var conn = Abrir(ConstruirDb());

        // "dbo." casa con las dos tablas y con sus columnas: no debe inventar una.
        var r = EvidenceQueries.Evidence(conn, Proc, "dbo.", 10);

        Assert.False(r.ContainsKey("pasos"));
        Assert.Contains("ambiguo", (string)r["reason"]!);
        Assert.NotEmpty((List<Dictionary<string, object?>>)r["candidatos"]!);
    }

    [Fact]
    public void Evidence_TargetQueElObjetoNoToca_NoDevuelvePasos()
    {
        using var conn = Abrir(ConstruirDb());

        // dbo.Otra existe en el grafo, pero la toca dbo.Ajeno, no dbo.Mueve.
        var r = EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.otra", 10);

        Assert.False(r.ContainsKey("pasos"));
        Assert.Contains("no coincide", (string)r["reason"]!);
    }

    // ── errores y presupuesto ───────────────────────────────────────────────

    [Theory]
    [InlineData("", "dbo.origen")]
    [InlineData(Proc, "")]
    [InlineData("TestDb::dbo.NoExiste", "dbo.origen")]
    public void Evidence_ArgumentosInvalidos_Lanzan(string objectId, string target)
    {
        using var conn = Abrir(ConstruirDb());
        Assert.Throws<McpToolException>(() => EvidenceQueries.Evidence(conn, objectId, target, 10));
    }

    [Fact]
    public void Evidence_SobreUnaTabla_LanzaEnVezDeDevolverVacio()
    {
        using var conn = Abrir(ConstruirDb());

        var ex = Assert.Throws<McpToolException>(
            () => EvidenceQueries.Evidence(conn, $"{Db}:table:dbo.origen", "dbo.origen", 10));

        Assert.Contains("no un SqlObject", ex.Message);
    }

    [Fact]
    public void Evidence_CabeEnElPresupuesto()
    {
        using var conn = Abrir(ConstruirDb());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(EvidenceQueries.Evidence(conn, Proc, $"{Db}:table:dbo.origen", 6));

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"evidence ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }
}
