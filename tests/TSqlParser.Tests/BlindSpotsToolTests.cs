using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

public class BlindSpotsToolTests : IDisposable
{
    private const string Db = "TestDb";
    private readonly List<string> _temporales = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _temporales)
            if (File.Exists(f)) File.Delete(f);
    }

    private string ConstruirDb(params (string Name, string Sql)[] fuentes)
    {
        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        var ruta = Path.Combine(Path.GetTempPath(), $"blind-spots-{Guid.NewGuid():n}.db");
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

    private static List<Dictionary<string, object?>> ListaDeObjetos(Dictionary<string, object?> r, string clave) =>
        (List<Dictionary<string, object?>>)r[clave]!;

    // ── Detección de dinámico sin resolver ──────────────────────────────────

    [Fact]
    public void BlindSpots_ProcConEXECDinamico_AparceEnLaLista()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.ConDinamico", @"
                CREATE PROCEDURE dbo.ConDinamico @sql NVARCHAR(MAX) AS
                BEGIN
                    EXEC (@sql);
                END"),
            ("dbo.SinDinamico", "CREATE PROCEDURE dbo.SinDinamico AS BEGIN SELECT * FROM dbo.Destino; END"),
        };
        using var conn = Abrir(ConstruirDb(fuentes));

        var r = BlindSpotsQueries.BlindSpots(conn, 20);

        var objetos = ListaDeObjetos(r, "objetos_con_dinamico_sin_resolver");
        Assert.NotEmpty(objetos);

        var conDinamico = objetos.FirstOrDefault(o => ((string)o["name"]!).EndsWith("ConDinamico", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(conDinamico);
        Assert.True((long)conDinamico["unresolved_steps"]! > 0);

        var sinDinamico = objetos.FirstOrDefault(o => ((string)o["name"]!).EndsWith("SinDinamico", StringComparison.OrdinalIgnoreCase));
        Assert.Null(sinDinamico);
    }

    [Fact]
    public void BlindSpots_TotalYTruncated()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.P1", "CREATE PROCEDURE dbo.P1 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
            ("dbo.P2", "CREATE PROCEDURE dbo.P2 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
        };
        using var conn = Abrir(ConstruirDb(fuentes));

        var r = BlindSpotsQueries.BlindSpots(conn, 1);

        Assert.Equal(2, (int)r["total"]!);
        Assert.True((bool?)r["truncated"] ?? false);
    }

    [Fact]
    public void BlindSpots_ProcsSinDinamico_DevuelveReason()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.Simple", "CREATE PROCEDURE dbo.Simple AS BEGIN SELECT * FROM dbo.T1; END"),
        };
        using var conn = Abrir(ConstruirDb(fuentes));

        var r = BlindSpotsQueries.BlindSpots(conn, 20);

        Assert.True(r.ContainsKey("reason"));
        Assert.Contains("ningún objeto de este grafo tiene SQL dinámico sin resolver", (string)r["reason"]!);
        Assert.DoesNotContain(r, kv => (string)kv.Key == "objetos_con_dinamico_sin_resolver");
    }

    [Fact]
    public void BlindSpots_SumaDeStepsDinamicos()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.P1", "CREATE PROCEDURE dbo.P1 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
        };
        using var conn = Abrir(ConstruirDb(fuentes));

        var r = BlindSpotsQueries.BlindSpots(conn, 20);

        Assert.True(r.ContainsKey("pasos_dinamicos_totales"));
        Assert.True((long)r["pasos_dinamicos_totales"]! >= 0);
    }

    [Fact]
    public void BlindSpots_CabeEnElPresupuesto()
    {
        var fuentes = new (string Name, string Sql)[]
        {
            ("dbo.P1", "CREATE PROCEDURE dbo.P1 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
            ("dbo.P2", "CREATE PROCEDURE dbo.P2 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
            ("dbo.P3", "CREATE PROCEDURE dbo.P3 @sql NVARCHAR(MAX) AS BEGIN EXEC (@sql); END"),
        };
        using var conn = Abrir(ConstruirDb(fuentes));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(BlindSpotsQueries.BlindSpots(conn, 20));

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"blind_spots ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }
}
