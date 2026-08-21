using System.Text.Json;
using Microsoft.Data.Sqlite;
using TSqlParser;
using Xunit.Abstractions;

namespace TSqlParser.Tests;

/// <summary>
/// Instrumento de medida, no arreglo, para los cuatro patrones sintacticos diagnosticados como
/// ciegos sobre el corpus DNN (eval/column-recall/blind-refs.md): subconsulta escalar en la
/// lista SELECT, IF EXISTS/IF NOT EXISTS, ORDER BY y OUTPUT inserted/deleted. Corpus y
/// ground-truth congelados en eval/blind-patterns/{corpus.json,expected-columns.json}.
///
/// La clave del diseno: compara contra el ESTADO DECLARADO en expected-columns.json, no contra
/// "todo debe salir". Una columna "cubierto" que deja de salir es una regresion; una columna
/// "ciego" que EMPIEZA a salir tambien hace fallar el test, con un mensaje que pide actualizar
/// el ground-truth. Eso convierte cada arreglo real en una edicion explicita de este fichero -
/// nadie puede arreglar un patron sin que este gate se lo exija, ni afirmar haberlo arreglado
/// sin que el motor lo demuestre.
/// </summary>
public class BlindPatternsGateTests
{
    private readonly ITestOutputHelper _output;

    public BlindPatternsGateTests(ITestOutputHelper output) => _output = output;

    private sealed record CorpusEntryRaw(string Name, string Sql);

    private sealed record ExpectedColumn(string Module, string Table, string Column, string Status, string Pattern, string Reason);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string CorpusPath() => Path.Combine(EvalCorpora.RepoRoot(), "eval", "blind-patterns", "corpus.json");
    private static string ExpectedPath() => Path.Combine(EvalCorpora.RepoRoot(), "eval", "blind-patterns", "expected-columns.json");

    private static List<CorpusEntryRaw> LoadCorpus() =>
        JsonSerializer.Deserialize<List<CorpusEntryRaw>>(File.ReadAllText(CorpusPath()), JsonOpts)!;

    private static List<ExpectedColumn> LoadExpected() =>
        JsonSerializer.Deserialize<List<ExpectedColumn>>(File.ReadAllText(ExpectedPath()), JsonOpts)!;

    /// <summary>
    /// Construye el grafo con la misma cadena que los demas gates (SqlAnalyzer.AnalyzeObject
    /// por objeto + GraphExporter.Build con columnas) y ademas lo vuelca a SQLite con
    /// SqliteExporter.Write: no solo para parecerse a los otros gates, sino para probar el
    /// mismo camino que consume un agente real (el export puede perder algo que el
    /// GraphPayload en memoria si tiene), no solo la representacion intermedia.
    /// </summary>
    private static (GraphPayload Graph, string SqlitePath) BuildGraphAndSqlite()
    {
        var entries = LoadCorpus();
        var results = entries.Select(e => SqlAnalyzer.AnalyzeObject(e.Name, e.Sql)).ToList();
        foreach (var r in results)
            Assert.True(r.Error is null, $"{r.ObjectName}: {r.Error}");

        var graph = GraphExporter.Build(results, includeColumns: true);

        var sqlitePath = Path.Combine(Path.GetTempPath(), $"blind-patterns-{Guid.NewGuid():n}.db");
        SqliteExporter.Write(graph, sqlitePath, "BlindPatternsDb", "TestProj");
        return (graph, sqlitePath);
    }

    /// <summary>
    /// Igual que BuildGraphRefs, pero leyendo las aristas de columna DESDE el SQLite recien
    /// escrito (nodes/edges + json_extract sobre props) en lugar del GraphPayload en memoria -
    /// para que este gate detecte tambien una regresion de SqliteExporter, no solo de AstWalker.
    /// </summary>
    private static HashSet<ColumnRef> LoadRefsFromSqlite(string sqlitePath)
    {
        using var conn = new SqliteConnection($"Data Source={sqlitePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT n_obj.id AS module_id,
                   json_extract(n_col.props,'$.table') AS tbl,
                   json_extract(n_col.props,'$.name') AS col
            FROM edges e
            JOIN nodes n_col ON n_col.id = e.dst AND n_col.label = 'Column'
            JOIN edges hs ON hs.type = 'HAS_STEP' AND hs.dst = e.src
            JOIN nodes n_obj ON n_obj.id = hs.src
            WHERE e.type IN ('READS_COLUMN','FILTERS_ON','WRITES_COLUMN')
            """;
        using var reader = cmd.ExecuteReader();
        var refs = new HashSet<ColumnRef>();
        while (reader.Read())
        {
            var moduleId = reader.GetString(0);
            var table = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var col = reader.IsDBNull(2) ? "" : reader.GetString(2);
            if (table.Length == 0 || col.Length == 0) continue;

            var idx = moduleId.IndexOf("::", StringComparison.Ordinal);
            var module = BlindRefs.Plain(idx >= 0 ? moduleId[(idx + 2)..] : moduleId);
            var entity = BlindRefs.Plain(table.Contains('.') ? table : "dbo." + table);
            refs.Add(new ColumnRef(module, entity, BlindRefs.Plain(col)));
        }
        return refs;
    }

    [Fact]
    public void BlindPatterns_MatchGroundTruth()
    {
        var (graph, sqlitePath) = BuildGraphAndSqlite();
        try
        {
            var refsMemoria = BlindRefs.BuildGraphRefs(graph);
            var refsSqlite = LoadRefsFromSqlite(sqlitePath);
            var expected = LoadExpected();

            var regresiones = new List<string>();
            var arreglosSinDeclarar = new List<string>();
            var divergeSqlite = new List<string>();

            foreach (var e in expected)
            {
                var key = new ColumnRef(e.Module, e.Table, e.Column);
                var enMemoria = refsMemoria.Contains(key);
                var enSqlite = refsSqlite.Contains(key);

                if (enMemoria != enSqlite)
                    divergeSqlite.Add($"{e.Module}: {e.Table}.{e.Column} - grafo={enMemoria} sqlite={enSqlite} (SqliteExporter pierde o inventa la arista)");

                if (e.Status == "cubierto" && !enMemoria)
                    regresiones.Add($"{e.Module}: {e.Table}.{e.Column} dejo de salir (esperado cubierto) - {e.Reason}");
                else if (e.Status == "ciego" && enMemoria)
                    arreglosSinDeclarar.Add($"{e.Module}: {e.Table}.{e.Column} ya no es ciega: actualiza expected-columns.json a cubierto - {e.Reason}");
            }

            Assert.True(regresiones.Count == 0, "Regresion de cobertura:\n" + string.Join("\n", regresiones));
            Assert.True(arreglosSinDeclarar.Count == 0, "Arreglo(s) sin declarar en el ground-truth:\n" + string.Join("\n", arreglosSinDeclarar));
            Assert.True(divergeSqlite.Count == 0, "El export a SQLite no coincide con el grafo en memoria:\n" + string.Join("\n", divergeSqlite));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }

    /// <summary>Control negativo: si la clave de comparacion estuviera rota (columna mal formada,
    /// conjunto vacio), ninguna columna "cubierto" existiria y el test de arriba fallaria por
    /// las razones equivocadas. Aqui se exige que al menos una columna "cubierto" declarada
    /// SI aparezca, para que un ColumnRef mal construido no pase inadvertido.</summary>
    [Fact]
    public void Measurement_IsSensitive_AtLeastOneCoveredColumnIsFound()
    {
        var (graph, sqlitePath) = BuildGraphAndSqlite();
        try
        {
            var refs = BlindRefs.BuildGraphRefs(graph);
            var cubiertas = LoadExpected().Where(e => e.Status == "cubierto").ToList();
            Assert.True(cubiertas.Count > 0, "expected-columns.json no declara ninguna columna 'cubierto': no hay control de no-regresion.");
            Assert.True(cubiertas.Any(e => refs.Contains(new ColumnRef(e.Module, e.Table, e.Column))),
                "Ninguna columna 'cubierto' aparece en el grafo: la comparacion esta rota o el corpus no analiza.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }

    /// <summary>Foto de un vistazo: recuento ciego/cubierto por patron, tal como esta el motor HOY.</summary>
    [Fact]
    public void ReportBlindCountsByPattern()
    {
        var (graph, sqlitePath) = BuildGraphAndSqlite();
        try
        {
            var refs = BlindRefs.BuildGraphRefs(graph);
            var expected = LoadExpected();

            _output.WriteLine($"{"patron",-24} {"ciegas",7} {"cubiertas",10}");
            foreach (var grupo in expected.GroupBy(e => e.Pattern).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ciegas = grupo.Count(e => e.Status == "ciego" && !refs.Contains(new ColumnRef(e.Module, e.Table, e.Column)));
                var cubiertas = grupo.Count(e => e.Status == "cubierto" && refs.Contains(new ColumnRef(e.Module, e.Table, e.Column)));
                _output.WriteLine($"{grupo.Key,-24} {ciegas,7} {cubiertas,10}");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        }
    }
}
