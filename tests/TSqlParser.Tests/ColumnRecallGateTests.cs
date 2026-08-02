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
/// mal", y los datos la refutaron: de las 1983 pérdidas con la columna vista pero colgada de
/// otra entidad, 1896 son oráculo=VISTA / motor=TABLA. El motor atraviesa la vista hasta las
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
    // TRUNCADOS, no redondeados: el informe imprime un decimal y poner el suelo en el valor
    // redondeado hace fallar al propio commit que lo mide (ya pasó dos veces).
    //   estricto  0,711405
    //   laxo      0,969734
    // La precisión global se informa pero no se gatea (ver nota en el test); quien
    // vigila la invención de aristas es el suelo POR CLASE de MinPrecisionByClass.
    /// <summary>
    /// Suelo de precisión POR CLASE de evidencia, medido sobre este corpus. Es la base de una
    /// puntuación de confianza defendible: si las aristas expandidas de un `SELECT *` aciertan
    /// el 98 % sobre 1.523 casos reales, esa arista vale 0,98 — y eso no es una opinión que
    /// discutir, es una cuenta. La precisión global (67,8 %) no sirve para esto: mezcla clases
    /// y esconde que la extracción directa acierta el 99,8 %.
    /// </summary>
    private static readonly (string Class, double Floor)[] MinPrecisionByClass =
    {
        ("direct",        0.997),   // medido 99,8 % sobre 3.870 aristas
        ("star_expanded", 0.975),   // medido 98,2 % sobre 1.697 aristas
    };

    private const double MinStrictRecall = 0.7114;
    private const double MinLooseRecall  = 0.9697;

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

    /// <summary>
    /// Igual que <see cref="BuildGraphRefs"/> pero agrupando por CÓMO se supo cada arista.
    /// Una precisión global engaña: mezcla lecturas escritas literalmente en el SQL con
    /// lecturas alcanzadas atravesando una vista, y el oráculo (que se para en la vista)
    /// no puede contener estas últimas. Sin separar clases, la global daba 67,8 % y la
    /// expansión de estrella parecía la peor clase del motor (43,7 %) cuando en realidad
    /// es de las mejores. Cada clase lleva su propio suelo.
    /// </summary>
    private static Dictionary<string, HashSet<Ref>> BuildGraphRefsByClass(GraphPayload graph)
    {
        var owner = graph.Relationships
            .Where(r => r.Type == "HAS_STEP")
            .GroupBy(r => r.EndNodeId)
            .ToDictionary(g => g.Key, g => g.First().StartNodeId, StringComparer.Ordinal);

        static string Prop(Dictionary<string, object> p, string key) =>
            p.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";
        static string Plain(string s) => s.Replace("[", "").Replace("]", "").ToLowerInvariant();

        var columns = new Dictionary<string, (string Entity, string Column)>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!n.Labels.Contains("Column")) continue;
            var table = Plain(Prop(n.Properties, "table"));
            var col = Plain(Prop(n.Properties, "name"));
            if (table.Length == 0 || col.Length == 0) continue;
            columns[n.Id] = (table.Contains('.') ? table : "dbo." + table, col);
        }

        // Pasos cuya lista de selección era "SELECT *": sus columnas no están escritas en
        // el SQL, se expandieron desde el DDL de la tabla.
        var starSteps = graph.Nodes
            .Where(n => n.Labels.Contains("Step")
                        && n.Properties.TryGetValue("select_star", out var s) && s is true)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        static string ModuleOf(string id)
        {
            var idx = id.IndexOf("::", StringComparison.Ordinal);
            return Plain(idx >= 0 ? id[(idx + 2)..] : id);
        }

        var byClass = new Dictionary<string, HashSet<Ref>>(StringComparer.Ordinal);
        foreach (var r in graph.Relationships)
        {
            if (!ColumnRefEdges.Contains(r.Type)) continue;
            if (!columns.TryGetValue(r.EndNodeId, out var col)) continue;
            var cls = r.Properties.ContainsKey("via_view") ? "via_view"
                    : starSteps.Contains(r.StartNodeId) ? "star_expanded"
                    : "direct";
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            (byClass.TryGetValue(cls, out var set) ? set : byClass[cls] = new HashSet<Ref>())
                .Add(new Ref(ModuleOf(objId), col.Entity, col.Column));
        }
        return byClass;
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

        // La precisión se mide SOLO sobre módulos para los que el oráculo aporta alguna
        // columna. En el resto (típicamente por una tabla #temp, que impide a las DMV
        // resolver dependencias a nivel columna) el ciego es el oráculo, y contar ahí
        // nuestras aristas como falsos positivos castiga al motor por un límite ajeno.
        //
        // No es cosmético: al expandir SELECT * sobre vistas, la precisión global "bajó"
        // de 68,06 % a 67,91 % mientras que la medida sobre módulos visibles SUBÍA de
        // 69,96 % a 70,24 %. La caída era puro efecto de mezcla. Es además el mismo
        // criterio que ya usa ColumnLineage_PrecisionPerEvidenceClass.
        var modulesSeenHere = oracle.Select(r => r.Module).ToHashSet(StringComparer.Ordinal);
        var judgeable = graph.Where(r => modulesSeenHere.Contains(r.Module)).ToHashSet();
        var precision = (double)judgeable.Count(oracle.Contains) / judgeable.Count;

        var report =
            $"corpus DNN: oráculo={oracle.Count} aristas_grafo={graph.Count}\n" +
            $"  recall estricto (módulo,ENTIDAD,columna) = {strictRecall:P1}  (suelo {MinStrictRecall:P1})\n" +
            $"  recall laxo     (módulo,columna)         = {looseRecall:P1}  (suelo {MinLooseRecall:P1})\n" +
            $"  precisión                                = {precision:P1}  (informativa, no gateada)\n" +
            $"  brecha de CONVENCIÓN (laxo - estricto)   = {looseRecall - strictRecall:P1}  " +
            "(mayoritariamente vistas atravesadas hasta la tabla base)";

        Assert.True(strictRecall >= MinStrictRecall, "Regresión en recall estricto.\n" + report);
        Assert.True(looseRecall >= MinLooseRecall, "Regresión en recall laxo.\n" + report);

        // La precisión GLOBAL se informa pero NO se gatea, y conviene saber por qué:
        // mezcla clases y su movimiento lo domina la proporción de aristas via_view, cuya
        // precisión contra este oráculo es del 4 % por construcción (la DMV se para en la
        // vista). Cada vez que el motor mejora y resuelve más lecturas a través de vistas,
        // la global BAJA aunque no empeore ni una clase. Pasó dos veces seguidas: al
        // expandir SELECT * sobre vistas (70,24 -> 70,05) y al recuperar las columnas
        // explícitas que acompañan a una estrella. Un suelo así no detecta regresiones,
        // solo castiga mejoras, y acabaría bajándose por costumbre hasta no significar nada.
        //
        // Quien vigila que el motor no invente aristas es ColumnLineage_PrecisionPerEvidenceClass,
        // con un suelo POR CLASE (direct >= 99,7 %, star_expanded >= 97,5 %). Eso es
        // estrictamente más fuerte que un suelo combinado, porque una clase no puede
        // degradarse escondida detrás de otra.
    }

    /// <summary>
    /// Precisión POR CLASE de evidencia, que es lo que puede sostener una puntuación de
    /// confianza: la confianza de una arista es la precisión histórica de su clase, medida
    /// sobre este corpus, no un número puesto a ojo.
    ///
    /// `via_view` queda deliberadamente FUERA de los suelos: son lecturas alcanzadas
    /// atravesando una vista hasta su tabla base, y el oráculo se para en la vista, así que
    /// por construcción casi ninguna está respaldada (~4 %). No es un fallo, es otra
    /// convención — y meterla en un suelo sería fijar como invariante un artefacto de la
    /// comparación. Se informa, no se gatea.
    /// </summary>
    [Fact]
    public void ColumnLineage_PrecisionPerEvidenceClass()
    {
        var oracle = LoadOracle();
        var (results, tableSchemas) = InputAnalyzer.Analyze(Path.Combine(EvalDir(), "dnn-corpus.json"));
        var byClass = BuildGraphRefsByClass(GraphExporter.Build(results, includeColumns: true, tableSchemas));

        // Módulos para los que el oráculo no aporta NINGUNA columna (typically por una tabla
        // #temp, que impide a las DMV resolver dependencias a nivel columna). Ahí el ciego es
        // el oráculo: juzgar nuestras aristas contra él sería contarlas mal.
        var modulesSeen = oracle.Select(r => r.Module).ToHashSet(StringComparer.Ordinal);

        var report = new List<string>();
        var failures = new List<string>();
        foreach (var (cls, floor) in MinPrecisionByClass)
        {
            var edges = byClass.TryGetValue(cls, out var set)
                ? set.Where(r => modulesSeen.Contains(r.Module)).ToHashSet()
                : new HashSet<Ref>();
            Assert.True(edges.Count > 0, $"La clase '{cls}' no produjo ninguna arista: la clasificación está rota.");
            var precision = (double)edges.Count(oracle.Contains) / edges.Count;
            report.Add($"  {cls,-14} {edges.Count,6} aristas   precisión {precision:P1}  (suelo {floor:P1})");
            if (precision < floor)
                failures.Add(cls);
        }

        // Mismo filtro modulesSeen que las clases gateadas: sin él esta línea informaba
        // 2.563 mientras las otras dos informaban filtradas, y comparar cifras de un mismo
        // informe calculadas con criterios distintos es como se llega a una conclusión falsa.
        var viaView = byClass.TryGetValue("via_view", out var vv)
            ? vv.Count(r => modulesSeen.Contains(r.Module))
            : 0;
        report.Add($"  {"via_view",-14} {viaView,6} aristas   (informativa, no gateada)");

        Assert.True(failures.Count == 0,
            "Cae la precisión de: " + string.Join(", ", failures) + "\n" + string.Join("\n", report));
    }

    /// <summary>
    /// Gate de emisión (tarea "confianza al consumidor"): hasta ahora la clase de evidencia
    /// (direct/star_expanded/via_view) se DERIVABA a posteriori en <see cref="BuildGraphRefsByClass"/>
    /// mirando `via_view` y `select_star`. Eso vale para medir, pero no para consumir: nada obligaba
    /// a que la propiedad `resolution` que un consumidor real lee desde SQLite (`props` JSON en
    /// `edges`) existiera, ni a que su valor casara con esa derivación.
    ///
    /// Este gate comprueba las DOS cosas sobre toda arista de columna (READS_COLUMN, WRITES_COLUMN,
    /// FILTERS_ON - el mismo <see cref="ColumnRefEdges"/> que usa el resto del fichero):
    ///   1. la propiedad `resolution` existe.
    ///   2. su valor coincide exactamente con la clasificación derivada (misma prioridad: via_view
    ///      gana sobre select_star, que gana sobre direct).
    ///
    /// Sin este gate, alguien puede tocar `GraphExporter`, dejar de escribir `resolution` en una
    /// rama nueva o en una ya existente, y la clase se degrada en silencio: el resto de tests de
    /// este fichero seguirían en verde porque derivan la clase por su cuenta, sin leer lo que el
    /// motor efectivamente escribió en la arista.
    /// </summary>
    [Fact]
    public void ColumnEdges_CarryResolutionProperty_MatchingDerivedClassification()
    {
        var (results, tableSchemas) = InputAnalyzer.Analyze(Path.Combine(EvalDir(), "dnn-corpus.json"));
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);

        // Misma regla que BuildGraphRefsByClass: pasos cuya lista de selección era "SELECT *".
        var starSteps = graph.Nodes
            .Where(n => n.Labels.Contains("Step")
                        && n.Properties.TryGetValue("select_star", out var s) && s is true)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var columnEdges = graph.Relationships.Where(r => ColumnRefEdges.Contains(r.Type)).ToList();
        Assert.True(columnEdges.Count > 5000,
            $"Se esperaban miles de aristas de columna (READS_COLUMN/WRITES_COLUMN/FILTERS_ON) sobre el corpus DNN, hay {columnEdges.Count}. La extracción se rompió.");

        var missing = new List<string>();
        var mismatched = new List<string>();
        foreach (var r in columnEdges)
        {
            if (!r.Properties.TryGetValue("resolution", out var resolutionObj) || resolutionObj is not string resolution)
            {
                missing.Add($"{r.Type} {r.StartNodeId} -> {r.EndNodeId}");
                continue;
            }

            var expected = r.Properties.ContainsKey("via_view") ? "via_view"
                         : starSteps.Contains(r.StartNodeId) ? "star_expanded"
                         : "direct";
            if (!string.Equals(resolution, expected, StringComparison.Ordinal))
                mismatched.Add($"{r.Type} {r.StartNodeId} -> {r.EndNodeId}: resolution=\"{resolution}\" esperado=\"{expected}\"");
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} de {columnEdges.Count} aristas de columna NO tienen la propiedad 'resolution':\n" +
            string.Join("\n", missing.Take(20)) + (missing.Count > 20 ? "\n  ..." : ""));
        Assert.True(mismatched.Count == 0,
            $"{mismatched.Count} de {columnEdges.Count} aristas tienen 'resolution' que NO casa con la clasificación derivada:\n" +
            string.Join("\n", mismatched.Take(20)) + (mismatched.Count > 20 ? "\n  ..." : ""));
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
