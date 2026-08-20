using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public static class BlindSpotsQueries
{
    public static Dictionary<string, object?> BlindSpots(SqliteConnection conn, int limit)
    {
        if (limit <= 0) limit = 20;
        limit = Math.Min(limit, 1000);

        List<(string Id, string Name, long UnresolvedSteps)> conDinamico;
        int totalSinResolver;
        long sumaStepsDinamicos;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, name, unresolved_dynamic_sql_steps FROM nodes " +
                "WHERE label = 'SqlObject' AND unresolved_dynamic_sql_steps > 0 " +
                "ORDER BY unresolved_dynamic_sql_steps DESC";
            conDinamico = new List<(string, string, long)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var name = reader.IsDBNull(1) ? id : reader.GetString(1);
                var unresolvedSteps = reader.GetInt64(2);
                conDinamico.Add((id, name, unresolvedSteps));
            }

            totalSinResolver = conDinamico.Count;

            // Suma de dynamic_sql_steps de todos los SqlObject (para contexto)
            using var sumCmd = conn.CreateCommand();
            sumCmd.CommandText = "SELECT COALESCE(SUM(dynamic_sql_steps), 0) FROM nodes WHERE label = 'SqlObject'";
            sumaStepsDinamicos = Convert.ToInt64(sumCmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException ex)
        {
            throw new McpToolException($"blind_spots: error al consultar nodos ({ex.Message}).");
        }

        if (totalSinResolver == 0)
        {
            return new Dictionary<string, object?>
            {
                ["reason"] = "ningún objeto de este grafo tiene SQL dinámico sin resolver. No significa que el motor lo vea todo: significa que todo el SQL dinámico de este corpus resolvió a texto literal.",
            };
        }

        var pagina = conDinamico.Take(limit)
            .Select(x => new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["name"] = x.Name,
                ["unresolved_steps"] = x.UnresolvedSteps,
            })
            .ToList();

        var result = new Dictionary<string, object?>
        {
            ["objetos_con_dinamico_sin_resolver"] = pagina,
            ["total"] = totalSinResolver,
            ["pasos_dinamicos_totales"] = sumaStepsDinamicos,
        };

        if (totalSinResolver > limit)
            result["truncated"] = true;

        return result;
    }
}
