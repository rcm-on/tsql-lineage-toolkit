using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Parser.Graph;

/// <summary>
/// Inverso de <see cref="SqliteExporter"/>: reconstruye un <see cref="GraphPayload"/> a
/// partir del store SQLite, para que consumidores del grafo (RiskAnalyzer, etc.) corran
/// sobre un store ya escrito sin volver a analizar SQL. Los valores de propiedades salen
/// como JsonElement (deserializados del JSON de la columna props), el mismo formato que
/// RiskAnalyzer ya tolera al leer graph_full.json.
/// </summary>
public static class GraphRehydrator
{
    public static GraphPayload Rehydrate(SqliteConnection conn)
    {
        var graph = new GraphPayload();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, label, props FROM nodes";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var properties = ParseProps(reader, 2);

                // "labels" viaja dentro de props (ver SqliteExporter) porque la columna
                // `label` solo guarda Labels[0]. Un store anterior a ese cambio no la
                // tiene: cae a la única label de la columna `label`.
                List<string> labels;
                if (properties.TryGetValue("labels", out var raw) && raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    labels = je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    properties.Remove("labels");
                }
                else
                {
                    labels = label.Length > 0 ? [label] : [];
                }

                graph.Nodes.Add(new GraphNode { Id = id, Labels = labels, Properties = properties });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT src, dst, type, props FROM edges";
            using var reader = cmd.ExecuteReader();
            var i = 0;
            while (reader.Read())
            {
                var src = reader.GetString(0);
                var dst = reader.GetString(1);
                var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var properties = ParseProps(reader, 3);

                graph.Relationships.Add(new GraphRel
                {
                    Id = $"r{i++}",
                    Type = type,
                    StartNodeId = src,
                    EndNodeId = dst,
                    Properties = properties,
                });
            }
        }

        return graph;
    }

    private static Dictionary<string, object> ParseProps(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return new Dictionary<string, object>();
        var json = reader.GetString(ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
    }
}
