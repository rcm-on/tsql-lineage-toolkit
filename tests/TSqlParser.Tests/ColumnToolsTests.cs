using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

/// <summary>
/// Herramientas de columna del MCP. Corpus propio: McpTests construye el grafo sin
/// columnas, y aquí hacen falta DERIVES_FROM y aristas de referencia de columna.
///
/// Cadena del corpus:  Origen.Precio -> Destino.Total -> Final.Suma
/// dbo.Lector lee Origen.Precio sin derivar nada. dbo.Dinamico ejecuta SQL construido en
/// una variable, que nunca resuelve: es el que alimenta el descargo "desconocido".
/// </summary>
public class ColumnToolsTests : IDisposable
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
            ("dbo.Copia",    "CREATE PROCEDURE dbo.Copia AS BEGIN INSERT INTO dbo.Destino (Total) SELECT Precio FROM dbo.Origen; END"),
            ("dbo.Copia2",   "CREATE PROCEDURE dbo.Copia2 AS BEGIN INSERT INTO dbo.Final (Suma) SELECT Total FROM dbo.Destino; END"),
            ("dbo.Lector",   "CREATE PROCEDURE dbo.Lector AS BEGIN SELECT Precio FROM dbo.Origen; END"),
            ("dbo.Dinamico", "CREATE PROCEDURE dbo.Dinamico @p NVARCHAR(100) AS BEGIN DECLARE @s NVARCHAR(MAX); SET @s = N'SELECT * FROM ' + @p; EXEC(@s); END"),
        };

        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        var ruta = Path.Combine(Path.GetTempPath(), $"col-test-{Guid.NewGuid():n}.db");
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

    // Los ids de tabla van en minusculas por convencion del exportador; el nombre de
    // columna conserva su caja.
    private static string Col(string tabla, string columna) => $"{Db}:table:dbo.{tabla.ToLowerInvariant()}:column:{columna}";

    private static List<Dictionary<string, object?>> Lista(Dictionary<string, object?> r, string clave) =>
        (List<Dictionary<string, object?>>)r[clave]!;

    // ── column_provenance ───────────────────────────────────────────────────

    [Fact]
    public void Provenance_SigueLaCadenaYOrdenaLoMasProfundoPrimero()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.ColumnProvenance(conn, Col("Final", "Suma"), depth: 5, limit: 20);
        var fuentes = Lista(r, "sources");

        Assert.Equal(2, (int)r["total"]!);
        // El orden ES el contrato: primero lo más profundo, que es lo que hay que arreglar antes.
        Assert.EndsWith("Origen:column:Precio", (string)fuentes[0]["id"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (int)fuentes[0]["hops"]!);
        Assert.EndsWith("Destino:column:Total", (string)fuentes[1]["id"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (int)fuentes[1]["hops"]!);
    }

    [Fact]
    public void Provenance_DepthUno_CortaLaCadena()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.ColumnProvenance(conn, Col("Final", "Suma"), depth: 1, limit: 20);

        Assert.Equal(1, (int)r["total"]!);
    }

    [Fact]
    public void Provenance_ColumnaBase_ExplicaElVacioYApuntaAImpact()
    {
        using var conn = Abrir(ConstruirDb());

        // Origen.Precio no se calcula a partir de nada, pero SÍ alimenta a otras. Un
        // "sources: []" pelado sería indistinguible de "no hay linaje".
        var r = McpTools.ColumnProvenance(conn, Col("Origen", "Precio"), depth: 5, limit: 20);

        Assert.Empty(Lista(r, "sources"));
        Assert.Contains("DERIVES_FROM", (string)r["reason"]!);
        Assert.Contains("column_impact", (string)r["hint"]!);
    }

    // ── column_impact ───────────────────────────────────────────────────────

    [Fact]
    public void Impact_SeparaObjetosQueLaReferencianDeColumnasDerivadas()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.ColumnImpact(conn, Col("Origen", "Precio"), depth: 5, limit: 20);

        var objetos = Lista(r, "objects").Select(o => (string)o["name"]!).ToList();
        Assert.Contains(objetos, n => n.EndsWith("Copia", StringComparison.Ordinal));
        Assert.Contains(objetos, n => n.EndsWith("Lector", StringComparison.Ordinal));

        var columnas = Lista(r, "columns");
        Assert.Contains(columnas, c => ((string)c["id"]!).EndsWith("Destino:column:Total", StringComparison.OrdinalIgnoreCase) && (int)c["hops"]! == 1);
        Assert.Contains(columnas, c => ((string)c["id"]!).EndsWith("Final:column:Suma", StringComparison.OrdinalIgnoreCase) && (int)c["hops"]! == 2);
    }

    [Fact]
    public void Impact_ReferenciaLiteral_EsSeguraYSinMotivo()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.ColumnImpact(conn, Col("Origen", "Precio"), depth: 3, limit: 20);

        Assert.All(Lista(r, "objects"), o =>
        {
            Assert.Equal("seguro", o["confianza"]);
            Assert.DoesNotContain("motivo", o.Keys); // el motivo solo acompaña a lo "probable"
        });
    }

    [Fact]
    public void Impact_DeclaraElDinamicoSinResolver_AunqueHayaResultados()
    {
        using var conn = Abrir(ConstruirDb());

        var r = McpTools.ColumnImpact(conn, Col("Origen", "Precio"), depth: 3, limit: 20);

        // Descargo incondicional: si supiéramos qué toca ese EXEC, habría resuelto. El
        // fallo que este test impide es que nodes.db venga NULL en los nodos Column y el
        // conteo salga 0 en silencio, que fue exactamente lo que pasó al implementarlo.
        var desconocido = (Dictionary<string, object?>)r["desconocido"]!;
        Assert.True((int)desconocido["objetos"]! >= 1);
        Assert.Contains("dinámico", (string)desconocido["motivo"]!);
    }

    // ── errores y presupuesto ───────────────────────────────────────────────

    [Fact]
    public void AmbasHerramientas_SobreUnNodoQueNoEsColumna_EncaminanAImpact()
    {
        using var conn = Abrir(ConstruirDb());
        var proc = $"{Db}::dbo.Lector";

        foreach (var accion in new Func<Dictionary<string, object?>>[]
                 {
                     () => McpTools.ColumnImpact(conn, proc, 3, 20),
                     () => McpTools.ColumnProvenance(conn, proc, 3, 20),
                 })
        {
            var ex = Assert.Throws<McpToolException>(() => accion());
            Assert.Contains("no una Column", ex.Message);
            Assert.Contains("impact", ex.Message);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("TestDb:table:dbo.NoExiste:column:Nada")]
    public void ColumnImpact_IdInvalido_Lanza(string id)
    {
        using var conn = Abrir(ConstruirDb());
        Assert.Throws<McpToolException>(() => McpTools.ColumnImpact(conn, id, 3, 20));
    }

    [Fact]
    public void ColumnImpact_ColumnaMasReferenciada_CabeEnElPresupuesto()
    {
        using var conn = Abrir(ConstruirDb());

        string masReferenciada;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dst FROM edges WHERE type IN ('READS_COLUMN','WRITES_COLUMN','FILTERS_ON') " +
                "GROUP BY dst ORDER BY COUNT(*) DESC LIMIT 1";
            masReferenciada = (string)cmd.ExecuteScalar()!;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(McpTools.ColumnImpact(conn, masReferenciada, 3, 15));

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"column_impact sobre {masReferenciada} ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }
}
