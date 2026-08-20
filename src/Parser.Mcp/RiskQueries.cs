using Microsoft.Data.Sqlite;
using Parser.Graph;

namespace Parser.Mcp;

/// <summary>
/// Manejador puro de risks: rehidrata el grafo del store (<see cref="GraphRehydrator"/>)
/// y le aplica <see cref="RiskAnalyzer"/> - el mismo motor de reglas que el dashboard,
/// corriendo aquí sobre el store SQLite en vez de sobre graph_full.json en el navegador.
/// </summary>
public static class RiskQueries
{
    // Orden de severidad de RiskAnalyzer (crit es la copia más severa). No se puede leer
    // de RiskAnalyzer.SevOrder (privado a propósito: es fontanería de ordenación interna,
    // no un contrato), así que se repite aquí solo el orden, no las reglas.
    private static readonly string[] SevOrder = { "crit", "high", "med", "low", "info" };

    // Techo propio (como diff_impact): un hallazgo lleva texto libre (Detail), así que ni
    // con el límite por defecto (15) cabe siempre en ResponseBudgetBytes. Calibrado contra
    // eval/bad-practices (38 hallazgos reales) - ver RiskToolTests.Risks_CabeEnElPresupuesto.
    private const int MaxFindings = 6;

    public static Dictionary<string, object?> Risks(SqliteConnection conn, string? severity, int limit)
    {
        if (limit <= 0) limit = 15;
        limit = Math.Min(limit, MaxFindings);

        var sevIndex = SevOrder.Length - 1; // default: info hacia arriba = todas
        if (!string.IsNullOrWhiteSpace(severity))
        {
            sevIndex = Array.IndexOf(SevOrder, severity.Trim().ToLowerInvariant());
            if (sevIndex < 0)
                throw new McpToolException(
                    $"risks: severity debe ser uno de {string.Join("/", SevOrder)}, no '{severity}'.");
        }

        List<RiskFinding> findings;
        bool datosDeEjecucion;
        try
        {
            var graph = GraphRehydrator.Rehydrate(conn);
            findings = RiskAnalyzer.Analyze(graph)
                .Where(f => Array.IndexOf(SevOrder, f.Sev) <= sevIndex)
                .ToList();
            datosDeEjecucion = TieneDatosDeEjecucion(conn);
        }
        catch (SqliteException ex)
        {
            // Rehidratar exige el esquema completo (props incluido). Un store parcial no es
            // "cero hallazgos": es otro esquema, y hay que decirlo en vez de fingir silencio.
            throw new McpToolException($"risks: el esquema del store no es el esperado ({ex.Message}).");
        }

        if (findings.Count == 0)
        {
            var result0 = new Dictionary<string, object?>
            {
                ["reason"] = "ninguna regla del conjunto actual disparó sobre este grafo. No significa que no haya riesgo: solo que ninguna de las reglas hoy implementadas encontró el patrón que busca.",
                ["datos_de_ejecucion"] = datosDeEjecucion,
            };
            if (!datosDeEjecucion) result0["advertencia"] = AdvertenciaEstructural;
            return result0;
        }

        var total = findings.Count;
        var pagina = findings.Take(limit).Select(f => new Dictionary<string, object?>
        {
            ["severidad"] = f.Sev,
            ["categoria"] = f.Cat,
            ["regla"] = f.Rule,
            ["componente"] = f.Component,
            ["detalle"] = f.Detail,
            // Todas las reglas actuales miran estructura estática, no ejecución real.
            ["evidencia"] = "estructural",
        }).ToList();

        var result = new Dictionary<string, object?>
        {
            ["hallazgos"] = pagina,
            ["total"] = total,
            ["datos_de_ejecucion"] = datosDeEjecucion,
        };
        if (total > limit) result["truncated"] = true;
        if (!datosDeEjecucion) result["advertencia"] = AdvertenciaEstructural;

        return result;
    }

    private const string AdvertenciaEstructural =
        "orden estructural: sin datos de ejecución no se sabe qué código se ejercita de verdad. " +
        "Un objeto complejo que corre dos veces al año y otro que corre en el camino caliente " +
        "reciben aquí el mismo veredicto.";

    // PlanEnricher marca confirmed_by="execution_plan" en aristas confirmadas por un plan y
    // source="execution_plan" en las que descubre; cualquiera de las dos basta como señal
    // barata (una consulta, sin recorrer el grafo en C#) de que el store pasó por enrich-from-plans.
    private static bool TieneDatosDeEjecucion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM edges WHERE json_extract(props,'$.confirmed_by') = 'execution_plan' " +
            "   OR json_extract(props,'$.source') = 'execution_plan' LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}
