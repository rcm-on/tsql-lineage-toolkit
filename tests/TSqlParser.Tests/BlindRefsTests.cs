using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate del subcomando <c>blind-refs</c>: comprueba que <see cref="BlindRefs.Compute"/> reproduce
/// exactamente la clasificación medida sobre el corpus DNN y que la lista de ciegas es,
/// literalmente, el mismo conjunto que <c>ColumnRecallGateTests</c> considera no cubierto - las
/// dos medidas comparten la misma función, así que no pueden divergir por construcción, pero
/// este test lo verifica en vez de darlo por supuesto.
///
/// Cifras congeladas aquí el 2026-08-15: 140 ciegas, recall laxo 98,0827 % (ver
/// <c>eval/column-recall/blind-refs.md</c>, causa #1 = subconsulta anidada en predicado/expresión
/// sin descender, 58/140). Tras el fix de ambiguedad de scope (AstWalker.ExtractFilterColumnsCore/
/// ScopedColumnCollector/ResolveScopedColumns: resuelve cada subconsulta en su propio scope FROM
/// en vez de fusionarlo con el de fuera) quedaron 122 ciegas, recall laxo 98,3292 %.
///
/// 2026-08-18, causa #2 = MERGE sin FILTERS_ON en el ON ni WRITES_COLUMN independiente del
/// éxito del lineage en WHEN MATCHED UPDATE SET (23/122). Fix en AstWalker.cs: el case
/// MergeStatement ahora pasa filterColumnsOverride desde la condición ON (vía
/// QualifiedColumnCollector + SplitColumnsByTable sobre las mismas mrgTableRefs que
/// MergeLineage), y MergeTargetColumns() añade a mrgColumns todo el SET de un WHEN MATCHED
/// UPDATE aunque MergeLineage no pudiera trazar el lado derecho a una tabla real (deliberadamente
/// SIN el WHEN NOT MATCHED INSERT - ver el comentario de MergeTargetColumns: incluirlo hundía la
/// precisión "direct" del 99,7846 % al 99,5 %, por debajo del suelo, porque el oráculo
/// sys.dm_sql_referenced_entities no reporta la lista de columnas INSERT de un MERGE). Medido:
/// 122 -&gt; 99 ciegas (exactamente las 23 de causa #2, verificado cruzando el CSV de blind-refs
/// antes/después: 23 caen, 0 nuevas), recall laxo 98,3292 % -&gt; 98,6442 %, sin regresión en
/// ninguna clase de precisión (direct 99,7846 %->99,7858 %, star_expanded sin cambio 98,8860 %).
///
/// 2026-08-18, causa #3 = TVF invocada como origen de filas, columnas de salida no
/// resueltas (17/99). Tres fixes: (1) AstWalker.CollectTableRefsInto registra alias-&gt;TVF para
/// cualquier SchemaObjectFunctionTableReference con SchemaObject, no solo sys.* -&gt; resuelve
/// alias.col en JOIN/APPLY. (2) GraphExporter: el branch TARGETS (step apunta a un TVF que SÍ
/// tiene SqlObject propio) solo daba READS_COLUMN para VIEW; ahora también para
/// TABLE_VALUED_FUNCTION/INLINE_TABLE_FUNCTION -&gt; resuelve "SELECT @v=Col FROM func()". (3)
/// ObjectResult.TvfOutputColumns (SqlAnalyzer: inline via ViewColumnLineage posicional,
/// multi-sentencia via RETURNS @t TABLE(...) declarado) + InputAnalyzer pre-pass -&gt; permite
/// "SELECT * FROM func()". Medido: 99 -&gt; 90 ciegas (9 de las 17 de causa #3 caen: 6 del fix 1, 3
/// del fix 2; el fix 3 no tenía fila en el corpus que lo necesitase, cubierto solo por test).
/// Las 8 restantes de causa #3 son gaps DISTINTOS, fuera de alcance: 4 con la condición JOIN
/// anidada dentro de una derived table en el FROM (adyacente a causa #1, no específico de TVF),
/// 3 con la columna referenciada solo en ORDER BY (causa #7), 1 con INSERT...SELECT * (la
/// expansión de estrella en INSERT no existe para ninguna tabla, no solo TVF). Recall laxo
/// 98,6442 % -&gt; 98,7675 %, sin regresión en precisión por clase (direct/star_expanded).
/// </summary>
public class BlindRefsTests
{
    private static CorpusEntry DnnCorpus() => EvalCorpora.Get("dnn");

    [Fact]
    public void Compute_OnDnnCorpus_MatchesFrozenClassification()
    {
        var corpus = DnnCorpus();
        var result = BlindRefs.Compute(corpus.InputPath(EvalCorpora.RepoRoot()), corpus.OraclePath(EvalCorpora.RepoRoot()));

        Assert.Equal(90, result.BlindCount);
        Assert.Equal(90, result.Blind.Count);
        Assert.Equal(0.987675, result.LooseRecall, 6);

        // Sin duplicados: Compute() sale de un HashSet<(Module,Column)>, así que esto es una
        // comprobación de que nada aguas abajo (ToList, orden) introdujo una copia.
        var distinct = result.Blind.Select(r => (r.Module, r.Column)).ToHashSet();
        Assert.Equal(result.Blind.Count, distinct.Count);
    }

    /// <summary>
    /// El conjunto de ciegas de <see cref="BlindRefs.Compute"/> tiene que ser EXACTAMENTE el mismo
    /// que "oráculo laxo menos grafo laxo" calculado por las piezas que usa
    /// <c>ColumnRecallGateTests.ColumnLineage_MeetsMeasuredFloors</c> (BlindRefs.LoadOracle +
    /// BlindRefs.BuildGraphRefs, que es la función que el gate llama). Si algún día alguien añade
    /// un paso intermedio solo al subcomando (o solo al gate), este test detecta la divergencia
    /// antes de que las dos cifras publicadas dejen de significar lo mismo.
    /// </summary>
    [Fact]
    public void Compute_BlindSet_MatchesGatesUncoveredSet()
    {
        var corpus = DnnCorpus();
        var repoRoot = EvalCorpora.RepoRoot();

        var result = BlindRefs.Compute(corpus.InputPath(repoRoot), corpus.OraclePath(repoRoot));

        // Misma ruta que Measure() en ColumnRecallGateTests: cargar oráculo + construir grafo por
        // separado, y derivar el conjunto laxo no cubierto exactamente como hace el gate.
        var oracle = BlindRefs.LoadOracle(corpus.OraclePath(repoRoot));
        var (results, tableSchemas) = InputAnalyzer.Analyze(corpus.InputPath(repoRoot));
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);
        var graphRefs = BlindRefs.BuildGraphRefs(graph);

        var oracleLoose = oracle.Select(r => (r.Module, r.Column)).ToHashSet();
        var graphLoose = graphRefs.Select(r => (r.Module, r.Column)).ToHashSet();
        var gateUncovered = oracleLoose.Where(r => !graphLoose.Contains(r)).ToHashSet();

        var computeBlind = result.Blind.Select(r => (r.Module, r.Column)).ToHashSet();

        Assert.Equal(gateUncovered.Count, computeBlind.Count);
        Assert.True(gateUncovered.SetEquals(computeBlind),
            "El conjunto de ciegas de BlindRefs.Compute diverge del conjunto no cubierto que mide el gate.");
    }
}
