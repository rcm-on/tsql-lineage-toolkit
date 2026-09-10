using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>Un módulo del catálogo y qué hizo el motor con él.</summary>
public readonly record struct ModuleCoverageRow(
    string Module,
    string Type,
    string Definition,
    string State,
    int LineageEdges,
    int UncoveredNodes,
    string Detail);

public sealed record ModuleCoverageResult(
    string Database,
    IReadOnlyList<ModuleCoverageRow> Rows)
{
    public int Total => Rows.Count;
    public int Count(string state) => Rows.Count(r => r.State == state);
}

/// <summary>
/// Reconciliación módulo a módulo entre el catálogo y el grafo.
///
/// <c>recall</c> mide referencias de COLUMNA: un módulo que no se extrae, que no parsea o que no
/// aporta ninguna referencia de columna no aparece en su medida ni para bien ni para mal. Aquí se
/// cruza el inventario de <c>sys.objects</c> — CLR y extendidos incluidos a propósito, para que
/// consten como no parseables en vez de desaparecer del recuento — contra lo que el extractor
/// trajo y contra lo que el grafo afirma de cada uno.
/// </summary>
public static class ModuleCoverage
{
    public const string Ok = "ok";
    public const string NoExtraido = "NO_EXTRAIDO";
    public const string NoParseablePorDiseno = "no_parseable_por_diseno";
    public const string ErrorParseo = "ERROR_PARSEO";
    public const string SinAristasConDml = "DEFECTO_SIN_ARISTAS";
    public const string SinAristasSinDml = "sin_aristas_sin_dml";

    private static readonly HashSet<string> AristasDeLineage = new(StringComparer.Ordinal)
    {
        "READS_FROM", "WRITES_TO", "READS_COLUMN", "WRITES_COLUMN", "FILTERS_ON", "TARGETS", "DERIVES_FROM",
    };

    private static readonly Regex TieneDml =
        new(@"\b(INSERT|UPDATE|DELETE|MERGE|SELECT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string SqlInventario = @"
SELECT LOWER(SCHEMA_NAME(o.schema_id) + '.' + o.name) AS modulo,
       o.type_desc,
       CASE WHEN OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1 THEN 'cifrado'
            WHEN m.object_id IS NULL THEN 'sin_definicion'
            ELSE 'ok' END AS definicion
FROM sys.objects o
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('P','V','FN','IF','TF','TR','PC','FS','FT','AF','X')
ORDER BY 1;";

    public static ModuleCoverageResult Compute(string database, string server)
    {
        var inventario = LeerInventario(database, server);
        if (inventario.Count == 0)
            throw new InvalidOperationException(
                $"El inventario de '{database}' salio vacio. Antes de concluir nada, comprueba la base, " +
                "el servidor y que el usuario tenga VIEW DEFINITION: una base sin un solo modulo propio " +
                "es mucho menos probable que una consulta que no puede ver nada.");

        var inputPath = Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():n}.json");
        try
        {
            if (ObjectExtractor.Run(database, inputPath, server, new List<string>(), null) != 0)
                throw new InvalidOperationException($"No se pudieron extraer los modulos de '{database}' en '{server}'.");

            var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
            var graph = GraphExporter.Build(results, includeColumns: true, tableSchemas);

            var extraidos = results.ToDictionary(r => SyntaxCoverage.ModuloDe(r.ObjectName), r => r, StringComparer.Ordinal);
            var tablas = tableSchemas.ToDictionary(t => SyntaxCoverage.ModuloDe(t.ObjectName), t => t, StringComparer.Ordinal);
            var aristas = ContarAristasPorModulo(graph);
            var sinCubrir = SyntaxCoverage.Analyze(inputPath)
                .Where(n => !n.Benign)
                .GroupBy(n => n.Module, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(n => n.Count), StringComparer.Ordinal);
            var cuerpos = LeerCuerpos(inputPath);

            var filas = new List<ModuleCoverageRow>();
            foreach (var (modulo, tipo, definicion) in inventario)
            {
                var n = aristas.TryGetValue(modulo, out var a) ? a : 0;
                var nodos = sinCubrir.TryGetValue(modulo, out var s) ? s : 0;

                var (estado, detalle) = Clasificar(
                    definicion,
                    extraido: extraidos.ContainsKey(modulo) || tablas.ContainsKey(modulo),
                    error: extraidos.TryGetValue(modulo, out var res) ? res.Error : null,
                    lineageEdges: n,
                    cuerpo: cuerpos.TryGetValue(modulo, out var c) ? c : "");

                filas.Add(new ModuleCoverageRow(modulo, tipo, definicion, estado, n, nodos, detalle));
            }

            return new ModuleCoverageResult(database, filas);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    /// <summary>
    /// El veredicto de un módulo. Un módulo extraído con DML en el cuerpo y cero aristas es
    /// DEFECTO, nunca "limpio": el motor no viendo nada no es el motor confirmando que no hay nada.
    /// </summary>
    internal static (string Estado, string Detalle) Clasificar(
        string definicion, bool extraido, string? error, int lineageEdges, string cuerpo)
    {
        if (definicion != "ok")
            return (NoParseablePorDiseno, definicion);

        if (!extraido)
            return (NoExtraido, "en el catalogo y ausente del input.json");

        if (error != null)
            return (ErrorParseo, error.Length > 200 ? error[..200] : error);

        if (lineageEdges == 0)
            return TieneDml.IsMatch(cuerpo)
                ? (SinAristasConDml, "el cuerpo trae DML y el grafo no afirma nada")
                : (SinAristasSinDml, "");

        return (Ok, "");
    }

    /// <summary>Aristas de lineage atribuidas a su objeto propietario (misma regla que BlindRefs).</summary>
    internal static Dictionary<string, int> ContarAristasPorModulo(GraphPayload graph)
    {
        var owner = graph.Relationships
            .Where(r => r.Type == "HAS_STEP")
            .GroupBy(r => r.EndNodeId)
            .ToDictionary(g => g.Key, g => g.First().StartNodeId, StringComparer.Ordinal);

        var cuenta = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in graph.Relationships)
        {
            if (!AristasDeLineage.Contains(r.Type)) continue;
            var objId = owner.TryGetValue(r.StartNodeId, out var o) ? o : r.StartNodeId.Split("#step")[0];
            var modulo = SyntaxCoverage.ModuloDe(objId);
            cuenta[modulo] = cuenta.TryGetValue(modulo, out var n) ? n + 1 : 1;
        }
        return cuenta;
    }

    private static Dictionary<string, string> LeerCuerpos(string inputPath)
    {
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sources = System.Text.Json.JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(inputPath), opts)
            ?? new List<SourceObject>();
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in sources)
            d[SyntaxCoverage.ModuloDe(s.Name)] = s.Sql ?? "";
        return d;
    }

    private static List<(string Modulo, string Tipo, string Definicion)> LeerInventario(string database, string server)
    {
        var salida = new List<(string, string, string)>();
        using var conn = new SqlConnection(SqlConnections.Build(server, database, timeoutSeconds: 30, SqlConnections.FromEnvironment()));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlInventario;
        cmd.CommandTimeout = 300;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            salida.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return salida;
    }

    public static void WriteCsv(ModuleCoverageResult result, string outputPath)
    {
        using var w = new StreamWriter(outputPath, append: false, Utf8Io.NoBom);
        w.WriteLine("module,type,definition,state,lineage_edges,uncovered_nodes,detail");
        foreach (var r in result.Rows.OrderBy(r => r.State == Ok).ThenBy(r => r.Module, StringComparer.Ordinal))
            w.WriteLine($"{Csv(r.Module)},{r.Type},{r.Definition},{r.State},{r.LineageEdges},{r.UncoveredNodes},{Csv(r.Detail)}");
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    public static string Summarize(ModuleCoverageResult r, string outputPath)
    {
        var lineas = new List<string>
        {
            $"coverage {r.Database}: {r.Total} modulo(s) en el catalogo -> {outputPath}",
            $"  ok={r.Count(Ok)}  NO_EXTRAIDO={r.Count(NoExtraido)}  ERROR_PARSEO={r.Count(ErrorParseo)}  " +
            $"DEFECTO_SIN_ARISTAS={r.Count(SinAristasConDml)}  sin_aristas_sin_dml={r.Count(SinAristasSinDml)}  " +
            $"no_parseable_por_diseno={r.Count(NoParseablePorDiseno)}",
        };

        if (r.Count(NoExtraido) > 0)
            lineas.Add($"  AVISO: {r.Count(NoExtraido)} modulo(s) que el motor nunca vio. No producen sintoma: " +
                       "el grafo sale limpio y falto. Suele ser VIEW DEFINITION o un tipo fuera de la consulta de extraccion.");

        if (r.Count(NoParseablePorDiseno) > 0)
            lineas.Add($"  {r.Count(NoParseablePorDiseno)} modulo(s) sin definicion legible (cifrado/CLR/extendido). " +
                       "No es un defecto, pero SI es cobertura perdida: va declarado en el informe.");

        return string.Join(Environment.NewLine, lineas);
    }
}
