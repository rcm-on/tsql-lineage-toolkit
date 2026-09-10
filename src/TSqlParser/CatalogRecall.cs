using System.Reflection;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// Recall del motor sobre UNA BASE VIVA CUALQUIERA, no sobre un corpus congelado.
///
/// Es la diferencia entre medir en casa y medir en casa del usuario. Hasta ahora el recall
/// solo se conocia para los corpus del repo (DNN, WWI), porque son los unicos cuyo catalogo
/// tenemos. Un usuario ejecuta el motor sobre su base y NO SABE si le faltan 40 referencias
/// o 400: el motor emite lo que ve y calla lo que no, y un silencio es indistinguible de un
/// "no hay nada".
///
/// Aqui se cierra ese hueco: se extraen los modulos de la base, se analiza, y se contrasta
/// contra el propio resolvedor de dependencias de SQL Server
/// (sys.dm_sql_referenced_entities) en esa misma base.
///
/// LIMITE QUE HAY QUE PUBLICAR SIEMPRE JUNTO A LA CIFRA: el catalogo de SQL Server tambien
/// es ciego al SQL dinamico. Una columna que solo se toca dentro de un EXEC(@sql) sin
/// resolver no aparece ni en el catalogo ni en el grafo, asi que NO cuenta como ciega: ese
/// hueco no lo ve ninguna de las dos partes. Por eso el resumen incluye el numero de
/// objetos con dinamico sin resolver, y por eso dar el recall a secas seria media verdad.
/// </summary>
public static class CatalogRecall
{
    /// <summary>El script vive en eval/column-recall/extract-catalog.sql y se embebe en el
    /// ensamblado: el tool instalado no tiene el repositorio al lado.</summary>
    private const string RecursoScript = "TSqlParser.extract-catalog.sql";

    public sealed record LiveRecallResult(
        string Database,
        BlindRefsResult Refs,
        int ModulesAnalyzed,
        int ParseErrors,
        int ObjectsWithUnresolvedDynamicSql,
        IReadOnlyList<BlindRef> Phantom,
        IReadOnlyList<BlindRef> PhantomFromDynamic,
        int ModulesInOracle,
        IReadOnlyList<BlindRef> PhantomOutsideOracle)
    {
        /// <summary>
        /// Fraccion de modulos sobre los que el oraculo dice ALGO. Es el dato que decide si el
        /// recall significa algo: un 100 % de recall medido sobre un tercio de la base no es un
        /// 100 %, es un tercio.
        /// </summary>
        public double OracleModuleCoverage => ModulesAnalyzed == 0 ? 0.0 : (double)ModulesInOracle / ModulesAnalyzed;
    }

    public static LiveRecallResult Compute(string database, string server)
    {
        // 1. Los modulos de la base, por la misma via que el subcomando extract.
        var inputPath = Path.Combine(Path.GetTempPath(), $"recall-{Guid.NewGuid():n}.json");
        try
        {
            var codigo = ObjectExtractor.Run(database, inputPath, server, new List<string>(), null);
            if (codigo != 0)
                throw new InvalidOperationException($"No se pudieron extraer los modulos de '{database}' en '{server}'.");

            var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
            var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);
            var graphRefs = BlindRefs.BuildGraphRefs(graph);

            // 2. La referencia: el propio resolvedor de dependencias de SQL Server.
            var catalogo = LeerCatalogo(database, server);

            var catalogoLaxo = catalogo.Select(r => (r.Module, r.Column)).ToHashSet();
            var grafoLaxo = graphRefs.Select(r => (r.Module, r.Column)).ToHashSet();

            var ciegas = catalogoLaxo
                .Where(r => !grafoLaxo.Contains(r))
                .Select(r => new BlindRef(r.Module, r.Column))
                .OrderBy(r => r.Module, StringComparer.Ordinal)
                .ThenBy(r => r.Column, StringComparer.Ordinal)
                .ToList();

            var recall = catalogoLaxo.Count == 0
                ? 0.0
                : (double)catalogoLaxo.Count(grafoLaxo.Contains) / catalogoLaxo.Count;

            var refs = new BlindRefsResult(
                CatalogRows: catalogo.Count,
                CatalogLooseRows: catalogoLaxo.Count,
                GraphRows: graphRefs.Count,
                GraphLooseRows: grafoLaxo.Count,
                LooseRecall: recall,
                Blind: ciegas);

            // La resta inversa: lo que el grafo afirma y el catalogo no declara. Separada por
            // origen porque el catalogo NO ve el SQL dinamico resuelto y el motor SI: acusar de
            // fantasma a lo que sale de ahi seria acusar al motor de su mejor funcion.
            var origenDinamico = OrigenDinamicoPorRef(graph);

            // Modulos sobre los que el oraculo dice ALGO. Un modulo con una sola referencia rota
            // pierde TODAS sus filas (SQL Server no liga la sentencia y el filtro
            // referenced_id IS NOT NULL se lleva por delante hasta la parte que si resolvia), asi
            // que en una base con objetos borrados el oraculo se queda mudo sobre medio catalogo
            // sin decirlo. Medido: 3 procedimientos, 2 con referencia rota -> recall 100 % sobre
            // el unico que resolvia.
            var modulosConOraculo = catalogoLaxo.Select(r => r.Module).ToHashSet(StringComparer.Ordinal);

            var fantasmas = new List<BlindRef>();
            var fantasmasDinamico = new List<BlindRef>();
            var fantasmasFueraDelOraculo = new List<BlindRef>();
            foreach (var r in grafoLaxo.Where(r => !catalogoLaxo.Contains(r)).OrderBy(r => r.Module, StringComparer.Ordinal).ThenBy(r => r.Column, StringComparer.Ordinal))
            {
                var destino =
                    origenDinamico.Contains(r) ? fantasmasDinamico
                    // Si el oraculo no declara NADA de ese modulo, no puede contradecir a nadie:
                    // no es sospecha de interpretacion erronea, es su propia zona ciega.
                    : !modulosConOraculo.Contains(r.Module) ? fantasmasFueraDelOraculo
                    : fantasmas;
                destino.Add(new BlindRef(r.Module, r.Column));
            }

            return new LiveRecallResult(
                Database: database,
                Refs: refs,
                ModulesAnalyzed: results.Count,
                ParseErrors: results.Count(r => r.Error != null),
                ObjectsWithUnresolvedDynamicSql: ContarObjetosConDinamicoSinResolver(graph),
                Phantom: fantasmas,
                PhantomFromDynamic: fantasmasDinamico,
                ModulesInOracle: modulosConOraculo.Count,
                PhantomOutsideOracle: fantasmasFueraDelOraculo);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    /// <summary>
    /// Referencias (modulo, columna) del grafo que provienen de un Step marcado como SQL dinamico.
    /// Misma resolucion de propietario que <see cref="BlindRefs.BuildGraphRefs"/>; lo unico que
    /// cambia es que aqui interesa la procedencia del Step, no la tripleta completa.
    /// </summary>
    private static HashSet<(string Module, string Column)> OrigenDinamicoPorRef(Parser.Contracts.GraphPayload graph)
    {
        var owner = graph.Relationships
            .Where(r => r.Type == "HAS_STEP")
            .GroupBy(r => r.EndNodeId)
            .ToDictionary(g => g.Key, g => g.First().StartNodeId, StringComparer.Ordinal);

        var pasosDinamicos = graph.Nodes
            .Where(n => n.Labels.Contains("Step")
                     && n.Properties.TryGetValue("is_dynamic_sql", out var d) && d is true)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        static string Prop(Dictionary<string, object> p, string key) =>
            p.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

        var columnas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!n.Labels.Contains("Column")) continue;
            var col = BlindRefs.Plain(Prop(n.Properties, "name"));
            if (col.Length > 0)
                columnas[n.Id] = col;
        }

        var salida = new HashSet<(string, string)>();
        foreach (var r in graph.Relationships)
        {
            if (!BlindRefs.ColumnRefEdges.Contains(r.Type)) continue;
            if (!pasosDinamicos.Contains(r.StartNodeId)) continue;
            if (!columnas.TryGetValue(r.EndNodeId, out var col)) continue;
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            var idx = objId.IndexOf("::", StringComparison.Ordinal);
            salida.Add((BlindRefs.Plain(idx >= 0 ? objId[(idx + 2)..] : objId), col));
        }
        return salida;
    }

    /// <summary>Objetos con al menos un paso de SQL dinamico que nunca resolvio a literal:
    /// el Step esta marcado como dinamico y su texto resuelto quedo vacio.</summary>
    private static int ContarObjetosConDinamicoSinResolver(Parser.Contracts.GraphPayload graph) =>
        graph.Nodes
            .Where(n => n.Labels.Contains("Step")
                     && n.Properties.TryGetValue("is_dynamic_sql", out var esDinamico) && esDinamico is true
                     && (!n.Properties.TryGetValue("dynamic_sql", out var texto) || string.IsNullOrEmpty(texto as string)))
            .Select(n => Parser.Contracts.StoreSchema.RollUpStep(n.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static HashSet<ColumnRef> LeerCatalogo(string database, string server)
    {
        var script = LeerRecurso();
        var salida = new HashSet<ColumnRef>();

        using var conn = new SqlConnection(SqlConnections.Build(server, database, timeoutSeconds: 30, SqlConnections.FromEnvironment()));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = script;
        cmd.CommandTimeout = 600;   // el cursor recorre todos los modulos: en una base grande tarda
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var partes = reader.GetString(0).Split('|');
            if (partes.Length != 3) continue;
            salida.Add(new ColumnRef(BlindRefs.Plain(partes[0]), BlindRefs.Plain(partes[1]), BlindRefs.Plain(partes[2])));
        }
        return salida;
    }

    private static string LeerRecurso()
    {
        var ensamblado = Assembly.GetExecutingAssembly();
        using var s = ensamblado.GetManifestResourceStream(RecursoScript)
            ?? throw new InvalidOperationException(
                $"No esta embebido '{RecursoScript}'. Recompila: el script se incluye desde eval/column-recall/extract-catalog.sql.");
        using var lector = new StreamReader(s);
        return lector.ReadToEnd();
    }

    /// <summary>
    /// Resumen de cara al usuario. Da la cifra Y su limite en la misma respuesta: publicar
    /// el recall sin decir que el catalogo tampoco ve el SQL dinamico seria media verdad.
    /// </summary>
    public static string Summarize(LiveRecallResult r, string outputPath)
    {
        var lineas = new List<string>
        {
            $"recall {r.Database}: catalogo={r.Refs.CatalogLooseRows} referencias de columna, " +
            $"el motor ve {r.Refs.GraphLooseRows} -> recall={r.Refs.LooseRecall:P4} " +
            $"({r.Refs.BlindCount} sin ver) -> {outputPath}",
            $"  modulos analizados: {r.ModulesAnalyzed}" +
            (r.ParseErrors > 0 ? $", {r.ParseErrors} con error de parseo (no analizados)" : ""),
        };

        // La cobertura del oraculo va ANTES que los fantasmas y en mayusculas cuando falla: un
        // recall alto medido sobre una fraccion de la base es la cifra mas peligrosa que puede
        // emitir esta herramienta, porque parece exactamente lo que uno quiere ver.
        if (r.ModulesInOracle < r.ModulesAnalyzed)
        {
            lineas.Add(
                $"  ATENCION: el oraculo solo declara algo de {r.ModulesInOracle} de los {r.ModulesAnalyzed} modulos " +
                $"({r.OracleModuleCoverage:P2}). El recall de arriba esta medido SOLO sobre esos, no sobre la base.");
            lineas.Add(
                "  Causa habitual: un modulo con UNA referencia rota (objeto borrado, cross-database, servidor " +
                "vinculado) pierde TODAS sus filas en dm_sql_referenced_entities, hasta las que si resolvian. " +
                "Cuanto mas rota esta la base, mejor pinta este numero. No lo publiques sin esta linea al lado.");
        }
        else
        {
            lineas.Add($"  El oraculo declara algo de los {r.ModulesAnalyzed} modulos: el recall cubre toda la base.");
        }

        lineas.Add(
            $"  fantasmas: {r.Phantom.Count} referencia(s) que el grafo afirma y el catalogo contradice" +
            (r.PhantomFromDynamic.Count > 0
                ? $" (+{r.PhantomFromDynamic.Count} desde dinamico resuelto, que el catalogo no puede ver: NO cuentan)"
                : "") +
            (r.PhantomOutsideOracle.Count > 0
                ? $" (+{r.PhantomOutsideOracle.Count} en modulos de los que el oraculo no declara nada: tampoco cuentan, " +
                  "no puede contradecir lo que no ve)"
                : "") +
            (r.Phantom.Count > 0 ? ". Sospechosas de interpretacion erronea: revisar." : "."));

        if (r.ObjectsWithUnresolvedDynamicSql > 0)
            lineas.Add(
                $"  AVISO: {r.ObjectsWithUnresolvedDynamicSql} objeto(s) con SQL dinamico sin resolver. " +
                "El catalogo de SQL Server tampoco los ve, asi que lo que toquen NO cuenta en ese recall: " +
                "es una zona ciega para las dos partes.");
        else if (r.ModulesInOracle >= r.ModulesAnalyzed)
            lineas.Add("  Sin SQL dinamico sin resolver: el recall cubre todo lo que el catalogo declara.");

        return string.Join(Environment.NewLine, lineas);
    }
}
