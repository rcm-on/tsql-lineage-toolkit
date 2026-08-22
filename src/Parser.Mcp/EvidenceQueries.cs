using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

public static class EvidenceQueries
{
    private const int MaxCondicion = 120;
    private const int MaxDynamicSql = 120;

    /// <summary>Etiquetas que un objeto puede tocar y de las que tiene sentido pedir evidencia.</summary>
    private static readonly string[] TargetLabels = ["Table", "Column"];

    public static Dictionary<string, object?> Evidence(SqliteConnection conn, string objectId, string target, int limit)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new McpToolException("evidence: 'object_id' no puede estar vacío.");
        if (string.IsNullOrWhiteSpace(target))
            throw new McpToolException("evidence: 'target' no puede estar vacío.");
        if (limit <= 0) limit = 6;
        limit = Math.Min(limit, 50);

        var objectName = ValidarObjeto(conn, objectId);
        var (targetId, targetName, candidatos) = ResolverTarget(conn, objectId, target.Trim());

        if (targetId == null)
        {
            return new Dictionary<string, object?>
            {
                ["object_id"] = objectId,
                ["reason"] = candidatos.Count == 0
                    ? $"'{target}' no coincide con ninguna tabla ni columna que {objectName} toque en este grafo."
                    : $"'{target}' es ambiguo dentro de {objectName}.",
                ["hint"] = candidatos.Count == 0
                    ? "Comprueba con describe_object qué tablas lee/escribe este objeto, o resuelve el nombre con resolve_object."
                    : "Vuelve a llamar con uno de los ids de 'candidatos'.",
                ["candidatos"] = candidatos.Take(limit).ToList(),
            };
        }

        var pasos = LeerPasos(conn, objectId, targetId);
        if (pasos.Count == 0)
        {
            // No debería ocurrir: el target salió de las propias aristas del objeto. Si
            // ocurre, es instrumento roto, no ausencia de evidencia.
            return new Dictionary<string, object?>
            {
                ["object_id"] = objectId,
                ["target_id"] = targetId,
                ["reason"] = $"{objectName} tiene una arista hacia {targetName} pero ningún paso la respalda; el store está incompleto o desactualizado.",
                ["hint"] = "Comprueba la antigüedad del grafo con store_info y vuelve a generarlo.",
            };
        }

        var result = new Dictionary<string, object?>
        {
            ["object_id"] = objectId,
            ["object_name"] = objectName,
            ["target_id"] = targetId,
            ["target_name"] = targetName,
            ["pasos"] = pasos.Take(limit).ToList(),
            ["total"] = pasos.Count,
            // El límite declarado, no el prometido: los Step guardan posición, no el texto.
            ["alcance"] = "línea y paso, no el SQL literal: line_no localiza la sentencia en el origen del objeto.",
        };
        if (pasos.Count > limit) result["truncated"] = true;
        return result;
    }

    private static string ValidarObjeto(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT label, name FROM nodes WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new McpToolException($"evidence: no existe ningún nodo con id '{id}'. Resuélvelo antes con resolve_object.");

        var label = reader.IsDBNull(0) ? "" : reader.GetString(0);
        if (label != "SqlObject")
            throw new McpToolException($"evidence: '{id}' es un nodo {label}, no un SqlObject. 'object_id' es el objeto que toca la tabla o columna, no la tabla o columna.");
        return reader.IsDBNull(1) ? id : reader.GetString(1);
    }

    /// <summary>
    /// Solo se acepta como target algo que este objeto toque de verdad: se busca entre los
    /// destinos de sus propias aristas, no en todo el grafo. Un nombre suelto que casa con
    /// varios devuelve candidatos en vez de elegir por su cuenta.
    /// </summary>
    private static (string? Id, string Name, List<Dictionary<string, object?>> Candidatos) ResolverTarget(
        SqliteConnection conn, string objectId, string target)
    {
        var alcanzables = new List<(string Id, string Label, string Name)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT DISTINCT n.id, n.label, n.name FROM edges e JOIN nodes n ON n.id = e.dst " +
                "WHERE (e.src = $obj OR e.src LIKE $obj || '#%') " +
                $"  AND n.label IN ({string.Join(",", TargetLabels.Select(l => $"'{l}'"))})";
            cmd.Parameters.AddWithValue("$obj", objectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                alcanzables.Add((reader.GetString(0),
                                 reader.IsDBNull(1) ? "" : reader.GetString(1),
                                 reader.IsDBNull(2) ? reader.GetString(0) : reader.GetString(2)));
        }

        var exacto = alcanzables.FirstOrDefault(a => string.Equals(a.Id, target, StringComparison.Ordinal));
        if (exacto.Id != null) return (exacto.Id, exacto.Name, []);

        // Sin esto, "dbo.Destino" sale ambiguo contra sus propias columnas, que lo
        // contienen como prefijo del id. El nombre exacto gana antes de mirar subcadenas.
        var porNombre = alcanzables.Where(a => string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (porNombre.Count == 1) return (porNombre[0].Id, porNombre[0].Name, []);

        var needle = target.ToLowerInvariant();
        var parciales = alcanzables
            .Where(a => a.Id.ToLowerInvariant().Contains(needle) || a.Name.ToLowerInvariant().Contains(needle))
            .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parciales.Count == 1) return (parciales[0].Id, parciales[0].Name, []);

        var candidatos = parciales
            .Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["label"] = a.Label })
            .ToList();
        return (null, target, candidatos);
    }

    private static List<Dictionary<string, object?>> LeerPasos(SqliteConnection conn, string objectId, string targetId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT e.src, e.type, e.props, n.props FROM edges e JOIN nodes n ON n.id = e.src " +
            "WHERE (e.src = $obj OR e.src LIKE $obj || '#%') AND e.dst = $dst AND n.label = 'Step'";
        cmd.Parameters.AddWithValue("$obj", objectId);
        cmd.Parameters.AddWithValue("$dst", targetId);

        var pasos = new List<(long Line, Dictionary<string, object?> Fila)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var src = reader.GetString(0);
            var tipo = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var propsArista = Parse(reader.IsDBNull(2) ? null : reader.GetString(2));
            var propsPaso = Parse(reader.IsDBNull(3) ? null : reader.GetString(3));

            var linea = Entero(propsPaso, "line_no");
            var fila = new Dictionary<string, object?>
            {
                ["paso"] = src.StartsWith(objectId + "#", StringComparison.Ordinal) ? src[(objectId.Length + 1)..] : src,
                ["line_no"] = linea,
                ["relacion"] = tipo,
            };

            Anadir(fila, "accion", Texto(propsPaso, "action"));
            Anadir(fila, "etiqueta", Texto(propsPaso, "label"));
            Anadir(fila, "resolution", Texto(propsArista, "resolution"));

            var opKinds = Lista(propsArista, "op_kinds");
            if (opKinds.Count > 0) fila["op_kinds"] = opKinds;

            var condicion = string.Join(" / ", Lista(propsPaso, "condition_keys"));
            Anadir(fila, "bajo_condicion", Recortar(condicion, MaxCondicion));

            if (Booleano(propsPaso, "is_dynamic_sql"))
            {
                fila["dinamico"] = true;
                Anadir(fila, "dynamic_sql", Recortar(Texto(propsPaso, "dynamic_sql"), MaxDynamicSql));
            }

            pasos.Add((linea ?? long.MaxValue, fila));
        }

        return pasos
            .OrderBy(p => p.Line)
            .ThenBy(p => (string?)p.Fila["paso"], StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Fila)
            .ToList();
    }

    private static void Anadir(Dictionary<string, object?> fila, string clave, string? valor)
    {
        if (!string.IsNullOrWhiteSpace(valor)) fila[clave] = valor;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? Texto(JsonElement? props, string clave) =>
        props is { } p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? Entero(JsonElement? props, string clave) =>
        props is { } p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    private static bool Booleano(JsonElement? props, string clave) =>
        props is { } p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> Lista(JsonElement? props, string clave)
    {
        if (props is not { } p || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(clave, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
