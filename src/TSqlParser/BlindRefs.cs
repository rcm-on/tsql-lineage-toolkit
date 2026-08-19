namespace TSqlParser;

/// <summary>
/// Una fila del oráculo o del grafo, a nivel (módulo, entidad, columna), todo en minúsculas. Único
/// tipo de clave que usa la comparación contra el oráculo de <c>eval/corpora.json</c>; ver
/// <see cref="BlindRefs"/> para el porqué de que viva en el motor y no en los tests.
/// </summary>
public readonly record struct ColumnRef(string Module, string Entity, string Column);

/// <summary>Una referencia (módulo, columna) del oráculo que el grafo NO reproduce, en el conjunto laxo.</summary>
public readonly record struct BlindRef(string Module, string Column);

/// <summary>Resultado de <see cref="BlindRefs.Compute"/>: los conteos que ya imprimía el gate más la
/// lista ordenada de referencias ciegas, que el gate solo agregaba.</summary>
public sealed record BlindRefsResult(
    int CatalogRows,
    int CatalogLooseRows,
    int GraphRows,
    int GraphLooseRows,
    double LooseRecall,
    IReadOnlyList<BlindRef> Blind)
{
    public int BlindCount => Blind.Count;
}

/// <summary>
/// Carga del oráculo de columna, construcción de las referencias del grafo y cálculo del recall
/// laxo — la MISMA lógica que usa <c>ColumnRecallGateTests</c> para medir el recall laxo, movida
/// aquí para que el subcomando <c>blind-refs</c> (que vuelca la LISTA de lo que falta, no solo el
/// agregado) no la reimplemente en paralelo. Dos copias de esta comparación divergirían en el
/// primer cambio de convención (p.ej. cómo se resuelve el propietario de un Step) y las dos
/// medidas dejarían de significar lo mismo sin que nada lo avisara.
/// </summary>
public static class BlindRefs
{
    /// <summary>
    /// Aristas del grafo que cuentan como "este módulo referencia esta columna", para casar con
    /// la semántica del oráculo: dm_sql_referenced_entities con minor_id &gt; 0 no distingue
    /// lectura de escritura, reporta cualquier referencia a la columna. Omitir WRITES_COLUMN
    /// descarta las columnas destino de todo UPDATE ... SET y hunde la medida 20 puntos. Ver
    /// ColumnRecallGateTests para el resto del razonamiento (esta lista es la misma).
    /// </summary>
    internal static readonly HashSet<string> ColumnRefEdges =
        new(StringComparer.Ordinal) { "READS_COLUMN", "FILTERS_ON", "WRITES_COLUMN" };

    internal static string Plain(string s) => s.Replace("[", "").Replace("]", "").ToLowerInvariant();

    /// <summary>Lee el catálogo PSV (módulo|entidad|columna) tal cual lo escribe extract-catalog.sql.</summary>
    internal static HashSet<ColumnRef> LoadCatalog(string catalogPsvPath)
    {
        var set = new HashSet<ColumnRef>();
        foreach (var line in File.ReadLines(catalogPsvPath))
        {
            var p = line.Split('|');
            if (p.Length == 3)
                set.Add(new ColumnRef(p[0], p[1], p[2]));
        }
        return set;
    }

    /// <summary>
    /// Extrae del grafo las lecturas de columna a nivel (módulo, entidad, columna). Las aristas
    /// salen de Steps, así que se resuelven a su objeto propietario por HAS_STEP (misma regla
    /// que NodeStoreExporter.OwnerOf).
    /// </summary>
    internal static HashSet<ColumnRef> BuildGraphRefs(GraphPayload graph)
    {
        var owner = graph.Relationships
            .Where(r => r.Type == "HAS_STEP")
            .GroupBy(r => r.EndNodeId)
            .ToDictionary(g => g.Key, g => g.First().StartNodeId, StringComparer.Ordinal);

        static string Prop(Dictionary<string, object> p, string key) =>
            p.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

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

        var refs = new HashSet<ColumnRef>();
        foreach (var r in graph.Relationships)
        {
            if (!ColumnRefEdges.Contains(r.Type)) continue;
            if (!columns.TryGetValue(r.EndNodeId, out var col)) continue;
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            refs.Add(new ColumnRef(ModuleOf(objId), col.Entity, col.Column));
        }
        return refs;
    }

    /// <summary>
    /// Analiza <paramref name="corpusJsonPath"/>, lo compara contra <paramref name="catalogPsvPath"/>
    /// al nivel laxo (módulo, columna) — la cobertura real, según razona el comentario de clase de
    /// <c>ColumnRecallGateTests</c> — y devuelve tanto los conteos agregados como la lista ORDENADA
    /// de referencias del catálogo que el grafo no reproduce.
    /// </summary>
    public static BlindRefsResult Compute(string corpusJsonPath, string catalogPsvPath)
    {
        var oracle = LoadCatalog(catalogPsvPath);
        var (results, tableSchemas) = InputAnalyzer.Analyze(corpusJsonPath);
        var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);
        var graphRefs = BuildGraphRefs(graph);

        var oracleLoose = oracle.Select(r => (r.Module, r.Column)).ToHashSet();
        var graphLoose = graphRefs.Select(r => (r.Module, r.Column)).ToHashSet();

        var blind = oracleLoose
            .Where(r => !graphLoose.Contains(r))
            .Select(r => new BlindRef(r.Module, r.Column))
            .OrderBy(r => r.Module, StringComparer.Ordinal)
            .ThenBy(r => r.Column, StringComparer.Ordinal)
            .ToList();

        var looseRecall = oracleLoose.Count == 0
            ? 0.0
            : (double)oracleLoose.Count(graphLoose.Contains) / oracleLoose.Count;

        return new BlindRefsResult(
            CatalogRows: oracle.Count,
            CatalogLooseRows: oracleLoose.Count,
            GraphRows: graphRefs.Count,
            GraphLooseRows: graphLoose.Count,
            LooseRecall: looseRecall,
            Blind: blind);
    }

    /// <summary>Escribe <paramref name="result"/> como CSV con cabecera "module,column".</summary>
    public static void WriteCsv(BlindRefsResult result, string outputPath)
    {
        using var writer = new StreamWriter(outputPath, append: false, Utf8Io.NoBom);
        writer.WriteLine("module,column");
        foreach (var r in result.Blind)
            writer.WriteLine($"{CsvField(r.Module)},{CsvField(r.Column)}");
    }

    private static string CsvField(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    /// <summary>Resumen de una línea, el mismo formato que imprimen los demás subcomandos.</summary>
    public static string Summarize(string corpusId, BlindRefsResult result, string outputPath) =>
        $"blind-refs {corpusId}: catálogo={result.CatalogRows} (laxo {result.CatalogLooseRows}) " +
        $"grafo_laxo={result.GraphLooseRows} recall_laxo={result.LooseRecall:P4} " +
        $"ciegas={result.BlindCount} -> {outputPath}";
}
