using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Trinquete del lineage de columna medido contra una referencia externo, sobre corpus grandes de
/// T-SQL real. Los corpus, sus referencias y sus suelos NO están aquí: se declaran en
/// <c>eval/corpora.json</c> y los lee <see cref="EvalCorpora"/>. Cada test es un
/// <c>[Theory]</c> que se ejecuta una vez por corpus gateado, así que añadir una base es
/// añadir una entrada de datos, no duplicar esta clase (ver el porqué en EvalCorpora).
///
/// A diferencia de <see cref="ViewLineageCatalogTests"/>, este gate NO necesita SQL Server:
/// tanto el corpus como la referencia están congelados en el repo, así que corre en cualquier
/// runner. El referencia se generó con sys.dm_sql_referenced_entities (referenced_minor_id &gt; 0)
/// sobre la base restaurada; ver eval/README.md para regenerarlo.
///
/// Mide TRES cosas, no una. El recall solo mide lo que falta; sin precisión, un motor que
/// inventara aristas pasaría el gate con nota. Ese fue exactamente el agujero que tenía
/// DbValidator con las aristas CALLS (solo miraba missing, nunca extra), así que aquí la
/// simetría es deliberada:
///
///   - recall laxo      (módulo, columna): la COBERTURA REAL. Qué fracción de las columnas
///     que ve la referencia ve también el motor, sin importar de qué entidad las cuelgue.
///   - recall estricto  (módulo, ENTIDAD, columna): concordancia literal con la referencia.
///   - precisión        (módulo, ENTIDAD, columna): qué fracción de lo que emite está
///     respaldada por la referencia.
///
/// OJO con la diferencia entre laxo y estricto. La primera lectura fue "el motor atribuye
/// mal", y los datos la refutaron: de las 1983 pérdidas con la columna vista pero colgada de
/// otra entidad, 1896 son referencia=VISTA / motor=TABLA. El motor atraviesa la vista hasta las
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
    /// la semántica de la referencia: dm_sql_referenced_entities con minor_id &gt; 0 no distingue
    /// lectura de escritura, reporta cualquier referencia a la columna. Omitir WRITES_COLUMN
    /// (el error de la primera versión de este gate) descarta las columnas destino de todo
    /// UPDATE ... SET y hunde la medida 20 puntos.
    ///
    /// CONSTRAINS, ASSIGNED_FROM, DERIVES_FROM y compañía se quedan FUERA a propósito: medido,
    /// no suben el recall ni una décima y desploman la precisión (66,9 % -> 42,7 %). No son
    /// referencias en el sentido de la referencia.
    /// </summary>
    // Movida a TSqlParser.BlindRefs.ColumnRefEdges: la usa tanto este fichero (BuildGraphRefsByClass,
    // más abajo) como el subcomando "blind-refs", y solo puede haber una lista.
    private static readonly HashSet<string> ColumnRefEdges = BlindRefs.ColumnRefEdges;

    // Los suelos viven en eval/corpora.json, uno por corpus. Están TRUNCADOS por debajo del
    // valor medido, no redondeados: el informe imprime un decimal y poner el suelo en el valor
    // redondeado hace fallar al propio commit que lo mide (ya pasó dos veces).
    //
    // El suelo de precisión es POR CLASE de evidencia, y esa es la base de una puntuación de
    // confianza defendible: si las aristas expandidas de un `SELECT *` aciertan el 98,88 % sobre
    // 3.493 casos reales, esa arista vale 0,9888 — y eso no es una opinión que discutir, es una
    // cuenta. La precisión GLOBAL no sirve para esto (ver la nota larga en
    // ColumnLineage_MeetsMeasuredFloors): mezcla clases y esconde que la extracción directa
    // acierta el 99,78 %.

    // LoadCatalog / BuildGraphRefs / Plain() se movieron a TSqlParser.BlindRefs: es la misma
    // comparación que ahora también usa el subcomando "blind-refs", y una sola fuente de verdad
    // es justo el punto (ver el comentario de clase de BlindRefs). ColumnRef sustituye al "Ref"
    // que antes era privado de este fichero.

    /// <summary>
    /// Igual que <see cref="BlindRefs.BuildGraphRefs"/> pero agrupando por CÓMO se supo cada arista.
    /// Una precisión global engaña: mezcla lecturas escritas literalmente en el SQL con
    /// lecturas alcanzadas atravesando una vista, y el catálogo (que se para en la vista)
    /// no puede contener estas últimas. Sin separar clases, la global daba 67,8 % y la
    /// expansión de estrella parecía la peor clase del motor (43,7 %) cuando en realidad
    /// es de las mejores. Cada clase lleva su propio suelo.
    /// </summary>
    private static Dictionary<string, HashSet<ColumnRef>> BuildGraphRefsByClass(GraphPayload graph)
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

        var byClass = new Dictionary<string, HashSet<ColumnRef>>(StringComparer.Ordinal);
        foreach (var r in graph.Relationships)
        {
            if (!ColumnRefEdges.Contains(r.Type)) continue;
            if (!columns.TryGetValue(r.EndNodeId, out var col)) continue;
            var cls = r.Properties.ContainsKey("via_view") ? "via_view"
                    : starSteps.Contains(r.StartNodeId) ? "star_expanded"
                    : "direct";
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            (byClass.TryGetValue(cls, out var set) ? set : byClass[cls] = new HashSet<ColumnRef>())
                .Add(new ColumnRef(ModuleOf(objId), col.Entity, col.Column));
        }
        return byClass;
    }

    private static GraphPayload BuildGraph(CorpusEntry corpus)
    {
        var (results, tableSchemas) = InputAnalyzer.Analyze(corpus.InputPath(EvalCorpora.RepoRoot()));
        return GraphExporter.Build(results, includeColumns: true, tableSchemas);
    }

    private static HashSet<ColumnRef> LoadCatalog(CorpusEntry corpus) =>
        BlindRefs.LoadCatalog(corpus.CatalogPath(EvalCorpora.RepoRoot()));

    private static (HashSet<ColumnRef> Catalog, HashSet<ColumnRef> Graph) Measure(CorpusEntry corpus) =>
        (LoadCatalog(corpus), BlindRefs.BuildGraphRefs(BuildGraph(corpus)));

    [Theory]
    [MemberData(nameof(EvalCorpora.GatedCorpusIds), MemberType = typeof(EvalCorpora))]
    public void ColumnLineage_MeetsMeasuredFloors(string corpusId)
    {
        var corpus = EvalCorpora.Get(corpusId);
        var floors = corpus.Floors!;
        var (catalog, graph) = Measure(corpus);

        var strictHits = catalog.Count(graph.Contains);
        var strictRecall = (double)strictHits / catalog.Count;

        var catalogLoose = catalog.Select(r => (r.Module, r.Column)).ToHashSet();
        var graphLoose = graph.Select(r => (r.Module, r.Column)).ToHashSet();
        var looseRecall = (double)catalogLoose.Count(graphLoose.Contains) / catalogLoose.Count;

        // La precisión se mide SOLO sobre módulos para los que el catálogo aporta alguna
        // columna. En el resto (típicamente por una tabla #temp, que impide a las DMV
        // resolver dependencias a nivel columna) el ciego es el catálogo, y contar ahí
        // nuestras aristas como falsos positivos castiga al motor por un límite ajeno.
        //
        // No es cosmético: al expandir SELECT * sobre vistas, la precisión global "bajó"
        // de 68,06 % a 67,91 % mientras que la medida sobre módulos visibles SUBÍA de
        // 69,96 % a 70,24 %. La caída era puro efecto de mezcla. Es además el mismo
        // criterio que ya usa ColumnLineage_PrecisionPerEvidenceClass.
        var modulesSeenHere = catalog.Select(r => r.Module).ToHashSet(StringComparer.Ordinal);
        var judgeable = graph.Where(r => modulesSeenHere.Contains(r.Module)).ToHashSet();
        var precision = (double)judgeable.Count(catalog.Contains) / judgeable.Count;

        var report =
            $"corpus {corpus.Id} ({corpus.Name}): catálogo={catalog.Count} aristas_grafo={graph.Count}\n" +
            $"  recall estricto (módulo,ENTIDAD,columna) = {strictRecall:P1}  (suelo {floors.StrictRecall:P1})\n" +
            $"  recall laxo     (módulo,columna)         = {looseRecall:P1}  (suelo {floors.LooseRecall:P1})\n" +
            $"  precisión                                = {precision:P1}  (informativa, no gateada)\n" +
            $"  brecha de CONVENCIÓN (laxo - estricto)   = {looseRecall - strictRecall:P1}  " +
            "(mayoritariamente vistas atravesadas hasta la tabla base)";

        Assert.True(strictRecall >= floors.StrictRecall, "Regresión en recall estricto.\n" + report);
        Assert.True(looseRecall >= floors.LooseRecall, "Regresión en recall laxo.\n" + report);

        // La precisión GLOBAL se informa pero NO se gatea, y conviene saber por qué:
        // mezcla clases y su movimiento lo domina la proporción de aristas via_view, cuya
        // precisión contra este referencia es del 4 % por construcción (la DMV se para en la
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
    /// atravesando una vista hasta su tabla base, y la referencia se para en la vista, así que
    /// por construcción casi ninguna está respaldada (~4 %). No es un fallo, es otra
    /// convención — y meterla en un suelo sería fijar como invariante un artefacto de la
    /// comparación. Se informa, no se gatea.
    /// </summary>
    [Theory]
    [MemberData(nameof(EvalCorpora.GatedCorpusIds), MemberType = typeof(EvalCorpora))]
    public void ColumnLineage_PrecisionPerEvidenceClass(string corpusId)
    {
        var corpus = EvalCorpora.Get(corpusId);
        var catalog = LoadCatalog(corpus);
        var byClass = BuildGraphRefsByClass(BuildGraph(corpus));

        // Módulos para los que el catálogo no aporta NINGUNA columna (typically por una tabla
        // #temp, que impide a las DMV resolver dependencias a nivel columna). Ahí el ciego es
        // el catálogo: juzgar nuestras aristas contra él sería contarlas mal.
        var modulesSeen = catalog.Select(r => r.Module).ToHashSet(StringComparer.Ordinal);

        var report = new List<string> { $"corpus {corpus.Id} ({corpus.Name}):" };
        var failures = new List<string>();
        foreach (var (cls, floor) in corpus.Floors!.PrecisionByClass)
        {
            var edges = byClass.TryGetValue(cls, out var set)
                ? set.Where(r => modulesSeen.Contains(r.Module)).ToHashSet()
                : new HashSet<ColumnRef>();
            Assert.True(edges.Count > 0, $"La clase '{cls}' no produjo ninguna arista: la clasificación está rota.");
            var precision = (double)edges.Count(catalog.Contains) / edges.Count;
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
    [Theory]
    [MemberData(nameof(EvalCorpora.GatedCorpusIds), MemberType = typeof(EvalCorpora))]
    public void ColumnEdges_CarryResolutionProperty_MatchingDerivedClassification(string corpusId)
    {
        var corpus = EvalCorpora.Get(corpusId);
        var graph = BuildGraph(corpus);

        // Misma regla que BuildGraphRefsByClass: pasos cuya lista de selección era "SELECT *".
        var starSteps = graph.Nodes
            .Where(n => n.Labels.Contains("Step")
                        && n.Properties.TryGetValue("select_star", out var s) && s is true)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var columnEdges = graph.Relationships.Where(r => ColumnRefEdges.Contains(r.Type)).ToList();
        Assert.True(columnEdges.Count >= corpus.Expected!.MinColumnEdges,
            $"Se esperaban al menos {corpus.Expected.MinColumnEdges} aristas de columna " +
            $"(READS_COLUMN/WRITES_COLUMN/FILTERS_ON) sobre el corpus {corpus.Id}, hay {columnEdges.Count}. " +
            "La extracción se rompió.");

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
    /// perturba la referencia renombrando cada columna y se exige que el recall se DESPLOME. Un gate
    /// que no puede fallar no es un gate.
    /// </summary>
    [Theory]
    [MemberData(nameof(EvalCorpora.GatedCorpusIds), MemberType = typeof(EvalCorpora))]
    public void Measurement_IsSensitive_ControlThatMustCollapse(string corpusId)
    {
        var corpus = EvalCorpora.Get(corpusId);
        var (catalog, graph) = Measure(corpus);

        // Igualdad EXACTA, no un ">": el catálogo es un fichero congelado. Si cambia de tamaño,
        // o se regeneró contra otra base o se truncó, y en cualquiera de los dos casos los
        // suelos de este corpus dejan de referirse a lo que se está midiendo. Actualizar el
        // corpus obliga a tocar el manifiesto, que es exactamente la disciplina que se busca.
        Assert.True(catalog.Count == corpus.Expected!.CatalogRows,
            $"El catálogo de '{corpus.Id}' declara {corpus.Expected.CatalogRows} filas y tiene {catalog.Count}. " +
            "Si el corpus se ha regenerado a propósito, actualiza eval/corpora.json en un commit " +
            "SEPARADO de cualquier cambio del motor.");
        // El suelo sale del manifiesto, no de un "> 1000" clavado aquí. Ese 1000 era una
        // suposición del tamaño de DNN, y el primer corpus que entró detrás (WWI-DW, 480
        // aristas: 24 procedimientos, no 739) la tumbó el mismo día. Es justo lo que se le
        // pide a un segundo corpus — destapar lo que estaba calibrado a ojo sobre el primero.
        Assert.True(graph.Count >= corpus.Expected.MinColumnEdges,
            $"El grafo de '{corpus.Id}' debería emitir al menos {corpus.Expected.MinColumnEdges} " +
            $"lecturas de columna, emite {graph.Count}.");

        var perturbed = catalog.Select(r => r with { Column = r.Column + "_zzz_no_existe" }).ToHashSet();
        var perturbedRecall = (double)perturbed.Count(graph.Contains) / perturbed.Count;

        Assert.True(perturbedRecall < 0.001,
            $"La medición no distingue un catálogo falso: recall con columnas inexistentes = {perturbedRecall:P2}. " +
            "La comparación está rota y los umbrales del otro test no significan nada.");
    }
}
