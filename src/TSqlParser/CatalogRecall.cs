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
        int ObjectsWithUnresolvedDynamicSql);

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

            return new LiveRecallResult(
                Database: database,
                Refs: refs,
                ModulesAnalyzed: results.Count,
                ParseErrors: results.Count(r => r.Error != null),
                ObjectsWithUnresolvedDynamicSql: ContarObjetosConDinamicoSinResolver(graph));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
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

        if (r.ObjectsWithUnresolvedDynamicSql > 0)
            lineas.Add(
                $"  AVISO: {r.ObjectsWithUnresolvedDynamicSql} objeto(s) con SQL dinamico sin resolver. " +
                "El catalogo de SQL Server tampoco los ve, asi que lo que toquen NO cuenta en ese recall: " +
                "es una zona ciega para las dos partes.");
        else
            lineas.Add("  Sin SQL dinamico sin resolver: el recall cubre todo lo que el catalogo declara.");

        return string.Join(Environment.NewLine, lineas);
    }
}
