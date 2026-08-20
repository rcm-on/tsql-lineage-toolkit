using Microsoft.Data.Sqlite;
using Parser.Graph;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate de fidelidad de GraphRehydrator: el grafo reconstruido desde el store SQLite
/// tiene que ser indistinguible del grafo en memoria a efectos de un consumidor
/// (RiskAnalyzer) - mismos nodos, mismas aristas, mismas labels completas (incluidos
/// los nodos con dos, p.ej. SqlObject+Process), y los mismos hallazgos de riesgo.
/// Sin este gate la rehidratación es una promesa; con él, es un hecho verificado.
/// </summary>
public class GraphRehydratorTests : IDisposable
{
    private const string Db = "TestDb";
    private readonly List<string> _temporales = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _temporales)
            if (File.Exists(f)) File.Delete(f);
    }

    private static (GraphPayload Graph, string DbPath) BuildAndWrite(params (string Name, string Sql)[] fuentes)
    {
        var resultados = fuentes.Select(f => SqlAnalyzer.AnalyzeObject($"{Db}::{f.Name}", f.Sql)).ToList();
        foreach (var r in resultados) Assert.Null(r.Error);

        var grafo = GraphExporter.Build(resultados, includeColumns: true);
        var ruta = Path.Combine(Path.GetTempPath(), $"rehydrator-{Guid.NewGuid():n}.db");
        SqliteExporter.Write(grafo, ruta, Db, "TestProj");
        return (grafo, ruta);
    }

    private string Registrar(string ruta)
    {
        _temporales.Add(ruta);
        return ruta;
    }

    private static SqliteConnection AbrirSoloLectura(string ruta) =>
        new(new SqliteConnectionStringBuilder { DataSource = ruta, Mode = SqliteOpenMode.ReadOnly }.ToString());

    // Corpus con: procedimiento normal (SqlObject+Process), inyección SQL (tainted var),
    // cursor+tx sin catch, UPDATE sin WHERE, tabla sin PK - cubre varias reglas de
    // RiskAnalyzer en varias categorías, para que el gate compare algo con sustancia.
    private static readonly (string Name, string Sql)[] CorpusConRiesgos =
    {
        ("dbo.Simple", "CREATE PROCEDURE dbo.Simple AS BEGIN SELECT * FROM dbo.T1; END"),
        ("dbo.Injection", @"
            CREATE PROCEDURE dbo.Injection @filtro NVARCHAR(100) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX);
                DECLARE @f NVARCHAR(100);
                SELECT @f = Nombre FROM dbo.Config WHERE Id = 1;
                SET @sql = N'SELECT * FROM dbo.T1 WHERE Nombre = ''' + @f + '''';
                EXEC (@sql);
            END"),
        ("dbo.CursorTx", @"
            CREATE PROCEDURE dbo.CursorTx AS
            BEGIN
                BEGIN TRANSACTION;
                DECLARE c CURSOR FOR SELECT Id FROM dbo.T1;
                OPEN c; CLOSE c; DEALLOCATE c;
                COMMIT;
            END"),
        ("dbo.NoWhere", "CREATE PROCEDURE dbo.NoWhere AS BEGIN UPDATE dbo.T1 SET Nombre = 'x'; END"),
    };

    [Fact]
    public void Rehydrate_TieneAlMenosUnNodoConDosLabels()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        // El propio grafo en memoria ya debe tener el caso multi-label (SqlObject+Process
        // en todo procedimiento no-trigger): si esto falla, el corpus no sirve de gate.
        var multiLabelOriginal = grafo.Nodes.Where(n => n.Labels.Count >= 2).ToList();
        Assert.NotEmpty(multiLabelOriginal);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);

        var multiLabelRehidratado = rehidratado.Nodes.Where(n => n.Labels.Count >= 2).ToList();
        Assert.Equal(multiLabelOriginal.Count, multiLabelRehidratado.Count);
    }

    [Fact]
    public void Rehydrate_NodosYAristas_MismoConteoYMismosIds()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);

        Assert.Equal(grafo.Nodes.Count, rehidratado.Nodes.Count);

        var idsOriginal = grafo.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var idsRehidratado = rehidratado.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(idsOriginal, idsRehidratado);

        // El exportador descarta aristas huérfanas (endpoint sin nodo); en este corpus
        // no las hay, así que el conteo debe coincidir exactamente.
        Assert.Equal(grafo.Relationships.Count, rehidratado.Relationships.Count);
    }

    [Fact]
    public void Rehydrate_ListasDeLabelsCompletas_PorNodo()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);
        var porId = rehidratado.Nodes.ToDictionary(n => n.Id);

        foreach (var n in grafo.Nodes)
        {
            Assert.True(porId.TryGetValue(n.Id, out var r), $"falta el nodo {n.Id} tras rehidratar");
            var esperado = n.Labels.OrderBy(l => l, StringComparer.Ordinal).ToList();
            var obtenido = r!.Labels.OrderBy(l => l, StringComparer.Ordinal).ToList();
            Assert.Equal(esperado, obtenido);
        }
    }

    [Fact]
    public void Rehydrate_TiposDeArista_CoincidenComoMultiset()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);

        // El Id de arista no sobrevive al store (SqliteExporter no lo persiste), así que
        // se compara por (tipo, origen, destino) como multiset, no por Id.
        string Clave(GraphRel r) => $"{r.Type}␟{r.StartNodeId}␟{r.EndNodeId}";
        var original = grafo.Relationships.Select(Clave).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var obtenido = rehidratado.Relationships.Select(Clave).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(original, obtenido);
    }

    [Fact]
    public void Rehydrate_PropiedadesRelevantes_SqlObject_Coinciden()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);
        var porId = rehidratado.Nodes.ToDictionary(n => n.Id);

        foreach (var n in grafo.Nodes.Where(n => n.Labels.Contains("SqlObject")))
        {
            var r = porId[n.Id];
            Assert.Equal(n.Properties["full_name"], r.Properties["full_name"].ToString());
            Assert.Equal(n.Properties["object_type"], r.Properties["object_type"].ToString());
            Assert.Equal((bool)n.Properties["has_transaction"], JsonBool(r.Properties["has_transaction"]));
            Assert.Equal((bool)n.Properties["has_cursor"], JsonBool(r.Properties["has_cursor"]));
        }
    }

    private static bool JsonBool(object v) =>
        v is System.Text.Json.JsonElement je ? je.ValueKind == System.Text.Json.JsonValueKind.True : (bool)v;

    [Fact]
    public void Rehydrate_RiskAnalyzer_ProduceLosMismosHallazgosQueElGrafoOriginal()
    {
        var (grafo, ruta) = BuildAndWrite(CorpusConRiesgos);
        Registrar(ruta);

        using var conn = AbrirSoloLectura(ruta);
        conn.Open();
        var rehidratado = GraphRehydrator.Rehydrate(conn);

        var hallazgosOriginal = RiskAnalyzer.Analyze(grafo);
        var hallazgosRehidratado = RiskAnalyzer.Analyze(rehidratado);

        Assert.NotEmpty(hallazgosOriginal);

        string Clave(RiskFinding f) => $"{f.Sev}|{f.Cat}|{f.Rule}|{f.Component}|{f.Detail}";
        var esperado = hallazgosOriginal.Select(Clave).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var obtenido = hallazgosRehidratado.Select(Clave).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(esperado, obtenido);
    }
}
