using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

/// <summary>
/// store_info y describe_object. Corpus propio: un proc que lee de una tabla y escribe en
/// otra, y llama a un segundo proc; suficiente para poblar tablas_leidas/tablas_escritas/
/// llama_a/llamado_por sin arrastrar el corpus de McpTests o ColumnToolsTests.
/// </summary>
public class InfoToolsTests : IDisposable
{
    private const string Db = "TestDb";
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
            ("dbo.Orquestador", "CREATE PROCEDURE dbo.Orquestador AS BEGIN EXEC dbo.Copia; END"),
            ("dbo.Copia",       "CREATE PROCEDURE dbo.Copia AS BEGIN INSERT INTO dbo.Destino (Total) SELECT Precio FROM dbo.Origen; END"),
        };

        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        var ruta = Path.Combine(Path.GetTempPath(), $"info-test-{Guid.NewGuid():n}.db");
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

    private static List<Dictionary<string, object?>> Lista(Dictionary<string, object?> r, string clave) =>
        (List<Dictionary<string, object?>>)r[clave]!;

    private static List<string> Nombres(Dictionary<string, object?> r, string clave) =>
        (List<string>)r[clave]!;

    // ── store_info ──────────────────────────────────────────────────────────

    [Fact]
    public void StoreInfo_DevuelveMetaYConteos()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.StoreInfo(conn);

        Assert.Equal(Db, r["database"]);
        Assert.Equal("TestProj", r["project"]);
        Assert.Equal("graph-sqlite-v1", r["format"]);
        Assert.True((int)r["node_count"]! > 0);
        Assert.True((int)r["edge_count"]! > 0);

        var nodos = Lista(r, "nodes_by_label");
        Assert.NotEmpty(nodos);
        Assert.Contains(nodos, n => (string)n["label"]! == "SqlObject");

        var aristas = Lista(r, "edges_by_type");
        Assert.NotEmpty(aristas);
        Assert.Contains(aristas, a => (string)a["type"]! == "CALLS");

        Assert.Equal(0, (int)r["objetos_con_dinamico_sin_resolver"]!);
    }

    [Fact]
    public void StoreInfo_StoreReciente_SinAviso()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.StoreInfo(conn);

        Assert.True((int)r["dias_desde_generado"]! < 30);
        Assert.DoesNotContain("aviso", r.Keys);
    }

    [Fact]
    public void StoreInfo_StoreViejo_LlevaAviso()
    {
        var ruta = ConstruirDb();
        using (var w = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ruta }.ToString()))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value = $v WHERE key = 'generated_at'";
            cmd.Parameters.AddWithValue("$v", DateTimeOffset.Now.AddDays(-45).ToString("o"));
            cmd.ExecuteNonQuery();
        }
        using var conn = Abrir(ruta);

        var r = McpTools.StoreInfo(conn);

        Assert.True((int)r["dias_desde_generado"]! >= 45);
        Assert.Contains("días", (string)r["aviso"]!);
    }

    [Fact]
    public void StoreInfo_CabeEnElPresupuesto()
    {
        using var conn = Abrir(ConstruirDb());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(McpTools.StoreInfo(conn));

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"store_info ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }

    // ── describe_object ─────────────────────────────────────────────────────

    [Fact]
    public void DescribeObject_ProcConTablasYLlamadas()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.DescribeObject(conn, $"{Db}::dbo.Copia", 10);

        Assert.Equal($"{Db}::dbo.Copia", r["id"]);
        Assert.Equal("PROCEDURE", r["object_type"]);

        var leidas = Nombres(r, "tablas_leidas");
        var escritas = Nombres(r, "tablas_escritas");
        Assert.Contains(leidas, n => n.EndsWith("Origen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(escritas, n => n.EndsWith("Destino", StringComparison.OrdinalIgnoreCase));

        var llamadoPor = Nombres(r, "llamado_por");
        Assert.Contains(llamadoPor, n => n.EndsWith("Orquestador", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescribeObject_OrquestadorLlamaACopia()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.DescribeObject(conn, $"{Db}::dbo.Orquestador", 10);

        var llamaA = Nombres(r, "llama_a");
        Assert.Contains(llamaA, n => n.EndsWith("Copia", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Nombres(r, "tablas_leidas"));
        Assert.Empty(Nombres(r, "tablas_escritas"));
    }

    [Fact]
    public void DescribeObject_EscalaresOmiteNulos()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.DescribeObject(conn, $"{Db}::dbo.Copia", 10);

        Assert.True(r.ContainsKey("total_steps"));
        Assert.True(r.ContainsKey("has_error_handling"));
        Assert.False((bool)r["has_error_handling"]!);
        // Ninguna clave del profile debe llevar valor null explícito: se omite, no se envía null.
        Assert.DoesNotContain(r, kv => kv.Value is null);
    }

    [Fact]
    public void DescribeObject_SobreUnaTabla_LanzaYEncamina()
    {
        using var conn = Abrir(ConstruirDb());
        string tabla;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM nodes WHERE label = 'Table' LIMIT 1";
            tabla = (string)cmd.ExecuteScalar()!;
        }

        var ex = Assert.Throws<McpToolException>(() => McpTools.DescribeObject(conn, tabla, 10));
        Assert.Contains("no un SqlObject", ex.Message);
        Assert.Contains("impact", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TestDb::dbo.NoExiste")]
    public void DescribeObject_IdInvalido_Lanza(string id)
    {
        using var conn = Abrir(ConstruirDb());
        Assert.Throws<McpToolException>(() => McpTools.DescribeObject(conn, id, 10));
    }

    [Fact]
    public void DescribeObject_CabeEnElPresupuesto()
    {
        using var conn = Abrir(ConstruirDb());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(McpTools.DescribeObject(conn, $"{Db}::dbo.Copia", 10));

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"describe_object ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }
}
