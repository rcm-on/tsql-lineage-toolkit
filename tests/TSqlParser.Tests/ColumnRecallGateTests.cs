using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Trinquete del lineage de columna medido contra un oráculo externo, sobre un corpus
/// grande de T-SQL real (DNN Platform / DotNetNuke, 739 módulos + 128 tablas).
///
/// A diferencia de <see cref="ViewLineageOracleTests"/>, este gate NO necesita SQL Server:
/// tanto el corpus como el oráculo están congelados en eval/column-recall/, así que corre
/// en cualquier runner. El oráculo se generó una vez con
/// sys.dm_sql_referenced_entities (referenced_minor_id &gt; 0) sobre la base restaurada;
/// ver eval/column-recall/README.md para regenerarlo.
///
/// Mide TRES cosas, no una. El recall solo mide lo que falta; sin precisión, un motor que
/// inventara aristas pasaría el gate con nota. Ese fue exactamente el agujero que tenía
/// DbValidator con las aristas CALLS (solo miraba missing, nunca extra), así que aquí la
/// simetría es deliberada:
///
///   - recall laxo      (módulo, columna): la COBERTURA REAL. Qué fracción de las columnas
///     que ve el oráculo ve también el motor, sin importar de qué entidad las cuelgue.
///   - recall estricto  (módulo, ENTIDAD, columna): concordancia literal con el oráculo.
///   - precisión        (módulo, ENTIDAD, columna): qué fracción de lo que emite está
///     respaldada por el oráculo.
///
/// OJO con la diferencia entre laxo y estricto. La primera lectura fue "el motor atribuye
/// mal", y los datos la refutaron: de las 1918 pérdidas con la columna vista pero colgada de
/// otra entidad, 1891 son oráculo=VISTA / motor=TABLA. El motor atraviesa la vista hasta las
/// tablas base y la DMV se para en la vista. Son convenciones distintas, no un defecto, y
/// para análisis de impacto la del motor es la útil. Por eso el recall estricto NO mide
/// calidad: es un detector de cambios en esa convención. La medida de calidad es el laxo.
///
/// Los umbrales son suelos medidos, no aspiraciones: si suben, se suben aquí. Ver
/// notes/task-column-recall.md para el plan de mejora.
/// </summary>
public class ColumnRecallGateTests
{
    /// <summary>
    /// Aristas del grafo que cuentan como "este módulo referencia esta columna", para casar con
    /// la semántica del oráculo: dm_sql_referenced_entities con minor_id &gt; 0 no distingue
    /// lectura de escritura, reporta cualquier referencia a la columna. Omitir WRITES_COLUMN
    /// (el error de la primera versión de este gate) descarta las columnas destino de todo
    /// UPDATE ... SET y hunde la medida 20 puntos.
    ///
    /// CONSTRAINS, ASSIGNED_FROM, DERIVES_FROM y compañía se quedan FUERA a propósito: medido,
    /// no suben el recall ni una décima y desploman la precisión (66,9 % -> 42,7 %). No son
    /// referencias en el sentido del oráculo.
    /// </summary>
    private static readonly HashSet<string> ColumnRefEdges =
        new(StringComparer.Ordinal) { "READS_COLUMN", "FILTERS_ON", "WRITES_COLUMN" };

    // Suelos medidos sobre este corpus (2026-08-01). Bajar de aquí es una regresión. Se ponen
    // truncados, no redondeados: el informe imprime 89,1 % pero el valor real es 0,890578, y
    // un suelo de 0,8908 haría fallar al propio commit que lo mide.
    //   estricto  5005/7786 = 0,642820
    //   laxo      6503/7302 = 0,890578
    //   precisión 5005/7480 = 0,669118
    private const double MinStrictRecall = 0.6428;
    private const double MinLooseRecall  = 0.8905;
    private const double MinPrecision    = 0.6691;

    private static string EvalDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "column-recall", "oracle-columns.psv")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontró eval/column-recall/ subiendo desde " + AppContext.BaseDirectory);
        return Path.Combine(dir!, "eval", "column-recall");
    }

    /// <summary>Una fila del oráculo: módulo, entidad referenciada y columna, todo en minúsculas.</summary>
    private readonly record struct Ref(string Module, string Entity, string Column);

    private static HashSet<Ref> LoadOracle()
    {
        var set = new HashSet<Ref>();
        foreach (var line in File.ReadLines(Path.Combine(EvalDir(), "oracle-columns.psv")))
        {
            var p = line.Split('|');
            if (p.Length == 3)
                set.Add(new Ref(p[0], p[1], p[2]));
        }
        return set;
    }

    /// <summary>
    /// Extrae del grafo las lecturas de columna a nivel (módulo, entidad, columna). Las aristas
    /// salen de Steps, así que se resuelven a su objeto propietario por HAS_STEP (misma regla
    /// que NodeStoreExporter.OwnerOf).
    /// </summary>
    private static HashSet<Ref> BuildGraphRefs(GraphPayload graph)
    {
        var owner = graph.Relationships
            .Where(r => r.Type == "HAS_STEP")
            .GroupBy(r => r.EndNodeId)
            .ToDictionary(g => g.Key, g => g.First().StartNodeId, StringComparer.Ordinal);

        static string Prop(Dictionary<string, object> p, string key) =>
            p.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

        static string Plain(string s) => s.Replace("[", "").Replace("]", "").ToLowerInvariant();

        // nodeId de columna -> (entidad, columna)
        var columns = new Dictionary<string, (string Entity, string Column)>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!n.Labels.Contains("Column")) continue;
            var table = Plain(Prop(n.Properties, "table"));
            var col = Plain(Prop(n.Properties, "name"));
            if (table.Length == 0 || col.Length == 0) continue;
            columns[n.Id] = (table.Contains('.') ? table : "dbo." + table, col);
        }

        static string ModuleOf(string id)
        {
            var idx = id.IndexOf("::", StringComparison.Ordinal);
            return Plain(idx >= 0 ? id[(idx + 2)..] : id);
        }

        var refs = new HashSet<Ref>();
        foreach (var r in graph.Relationships)
        {
            if (!ColumnRefEdges.Contains(r.Type)) continue;
            if (!columns.TryGetValue(r.EndNodeId, out var col)) continue;
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            refs.Add(new Ref(ModuleOf(objId), col.Entity, col.Column));
        }
        return refs;
    }

    private static (HashSet<Ref> Oracle, HashSet<Ref> Graph) Measure()
    {
        var oracle = LoadOracle();
        var (results, tableSchemas) = InputAnalyzer.Analyze(Path.Combine(EvalDir(), "dnn-corpus.json"));
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);
        return (oracle, BuildGraphRefs(graph));
    }

    [Fact]
    public void ColumnLineage_MeetsMeasuredFloors()
    {
        var (oracle, graph) = Measure();

        var strictHits = oracle.Count(graph.Contains);
        var strictRecall = (double)strictHits / oracle.Count;

        var oracleLoose = oracle.Select(r => (r.Module, r.Column)).ToHashSet();
        var graphLoose = graph.Select(r => (r.Module, r.Column)).ToHashSet();
        var looseRecall = (double)oracleLoose.Count(graphLoose.Contains) / oracleLoose.Count;

        var precision = (double)strictHits / graph.Count;

        var report =
            $"corpus DNN: oráculo={oracle.Count} aristas_grafo={graph.Count}\n" +
            $"  recall estricto (módulo,ENTIDAD,columna) = {strictRecall:P1}  (suelo {MinStrictRecall:P1})\n" +
            $"  recall laxo     (módulo,columna)         = {looseRecall:P1}  (suelo {MinLooseRecall:P1})\n" +
            $"  precisión                                = {precision:P1}  (suelo {MinPrecision:P1})\n" +
            $"  brecha de CONVENCIÓN (laxo - estricto)   = {looseRecall - strictRecall:P1}  " +
            "(mayoritariamente vistas atravesadas hasta la tabla base)";

        Assert.True(strictRecall >= MinStrictRecall, "Regresión en recall estricto.\n" + report);
        Assert.True(looseRecall >= MinLooseRecall, "Regresión en recall laxo.\n" + report);
        Assert.True(precision >= MinPrecision, "Regresión en precisión (el motor emite más aristas sin respaldo).\n" + report);
    }

    /// <summary>
    /// Control negativo: si la comparación estuviera rota (clave de join mal formada, conjuntos
    /// vacíos, normalización que iguala todo), el test de arriba pasaría sin medir nada. Aquí se
    /// perturba el oráculo renombrando cada columna y se exige que el recall se DESPLOME. Un gate
    /// que no puede fallar no es un gate.
    /// </summary>
    [Fact]
    public void Measurement_IsSensitive_ControlThatMustCollapse()
    {
        var (oracle, graph) = Measure();

        Assert.True(oracle.Count > 7000, $"El oráculo debería tener ~7786 filas, tiene {oracle.Count}");
        Assert.True(graph.Count > 1000, $"El grafo debería emitir miles de lecturas de columna, emite {graph.Count}");

        var perturbed = oracle.Select(r => r with { Column = r.Column + "_zzz_no_existe" }).ToHashSet();
        var perturbedRecall = (double)perturbed.Count(graph.Contains) / perturbed.Count;

        Assert.True(perturbedRecall < 0.001,
            $"La medición no distingue un oráculo falso: recall con columnas inexistentes = {perturbedRecall:P2}. " +
            "La comparación está rota y los umbrales del otro test no significan nada.");
    }
}
