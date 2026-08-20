using System.Text.Json;
using Parser.Graph;

namespace Parser.Mcp;

/// <summary>
/// Manejador puro de diff_impact: envuelve <see cref="ChangeMapDiff"/> (que ya sabe
/// diferenciar dos node stores) para responder la pregunta de un revisor de PR - "¿esto
/// alcanza algo que antes no alcanzaba?" - con la forma delgada que esperan las demás
/// herramientas MCP (total/truncated por lista, reason cuando no hay impacto nuevo).
/// ChangeMapDiff.Run escribe un fichero; aquí se corre contra un temporal, se relee y se
/// tira, para no dejar rastro en disco por cada llamada.
/// </summary>
public static class DiffQueries
{
    private static readonly JsonSerializerOptions DiffOutputOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // new_impact entries are richer than the id-only lists the other MCP tools page
    // (each carries up to 3 sub-lists of objects), so `limit` pages them but never past
    // these ceilings - otherwise a real PR touching 20+ objects blows the response budget
    // even though every other tool's default limit fits it comfortably.
    private const int MaxEntries = 8;
    private const int MaxSubList = 5;

    public static Dictionary<string, object?> Diff(string before, string after, int limit)
    {
        if (string.IsNullOrWhiteSpace(before))
            throw new McpToolException("diff_impact: 'before' no puede estar vacío.");
        if (string.IsNullOrWhiteSpace(after))
            throw new McpToolException("diff_impact: 'after' no puede estar vacío.");
        if (limit <= 0) limit = 20;
        limit = Math.Min(limit, 200);

        ValidarStore(before, "before");
        ValidarStore(after, "after");

        var tempOut = Path.Combine(Path.GetTempPath(), $"diff-impact-{Guid.NewGuid():n}.json");
        string diffJson;
        try
        {
            var exit = ChangeMapDiff.Run(before, after, tempOut, failOnNewImpact: false, DiffOutputOptions);
            if (exit == 1)
                throw new McpToolException(
                    $"diff_impact: no se pudo calcular el diff entre '{before}' y '{after}': " +
                    "falta manifest.json o change_map.json en alguno de los dos stores.");
            diffJson = File.ReadAllText(tempOut);
        }
        finally
        {
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }

        using var diffDoc = JsonDocument.Parse(diffJson);
        var root = diffDoc.RootElement;

        var objectsChanged = Strings(root.GetProperty("objects_changed"));
        var objectsAdded = Strings(root.GetProperty("objects_added"));
        var objectsRemoved = Strings(root.GetProperty("objects_removed"));

        var impactDelta = root.GetProperty("impact_delta");
        var entryNames = impactDelta.EnumerateObject().Select(p => p.Name)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        var entryLimit = Math.Min(limit, MaxEntries);
        var subLimit = Math.Min(limit, MaxSubList);
        var newImpactEntries = entryNames.Take(entryLimit).Select(name =>
        {
            var e = impactDelta.GetProperty(name);
            var viaCalls = ViaCallsAdded(e.GetProperty("via_calls_added"), subLimit, out var viaCallsTrunc);
            var viaData = ViaDataAdded(e.GetProperty("via_data_added"), subLimit, out var viaDataTrunc);
            var newlyAffected = TrimStrings(e.GetProperty("newly_affected"), subLimit, out var affectedTrunc);

            var entry = new Dictionary<string, object?> { ["object"] = name };
            if (viaCalls.Count > 0) entry["via_calls_added"] = viaCalls;
            if (viaData.Count > 0) entry["via_data_added"] = viaData;
            if (newlyAffected.Count > 0) entry["newly_affected"] = newlyAffected;
            if (viaCallsTrunc || viaDataTrunc || affectedTrunc) entry["truncated"] = true;
            return (object)entry;
        }).ToList();

        var summary = root.GetProperty("summary");
        var newlyAffectedTotal = summary.GetProperty("newly_affected_total").GetInt32();
        var riskNoteProp = summary.GetProperty("risk_note");
        var riskNote = riskNoteProp.ValueKind == JsonValueKind.String ? riskNoteProp.GetString() : null;

        var workflowsDelta = root.GetProperty("workflows_delta");
        var workflowsAdded = Strings(workflowsDelta.GetProperty("added"));
        var workflowsRemoved = Strings(workflowsDelta.GetProperty("removed"));
        var workflowsReshapedTotal = workflowsDelta.GetProperty("reshaped").GetArrayLength();

        var newImpact = new Dictionary<string, object?>
        {
            ["entries"] = newImpactEntries,
            ["total"] = entryNames.Count,
        };
        if (entryNames.Count > entryLimit) newImpact["truncated"] = true;

        // Regla del cero culpable: un new_impact vacío nunca sale sin explicar por qué -
        // "antes y después son iguales" no es lo mismo que "cambiaron cosas mudas".
        if (newlyAffectedTotal == 0)
        {
            var sinCambios = objectsChanged.Count == 0 && objectsAdded.Count == 0 && objectsRemoved.Count == 0;
            newImpact["reason"] = sinCambios
                ? "before y after son idénticos: mismo manifest.json y mismo change_map.json en ambos stores; no hay nada que comparar."
                : $"{objectsChanged.Count + objectsAdded.Count} objeto(s) cambiaron o se añadieron, pero ninguno introduce impacto nuevo " +
                  "(mismos via_calls/via_data que en 'before').";
        }

        var result = new Dictionary<string, object?>
        {
            ["objects_changed"] = Section(objectsChanged, limit),
            ["objects_added"] = Section(objectsAdded, limit),
            ["objects_removed"] = Section(objectsRemoved, limit),
            ["new_impact"] = newImpact,
            ["workflows_added"] = Section(workflowsAdded, limit),
            ["workflows_removed"] = Section(workflowsRemoved, limit),
            ["workflows_reshaped_total"] = workflowsReshapedTotal,
            ["newly_affected_total"] = newlyAffectedTotal,
        };
        if (riskNote != null) result["risk_note"] = riskNote;

        return result;
    }

    private static void ValidarStore(string dir, string cual)
    {
        if (!Directory.Exists(dir))
            throw new McpToolException(
                $"diff_impact: '{cual}' no es un directorio existente ('{dir}'). Se espera un store " +
                ".nodes (generado con --nodestore o update-nodestore) que contenga manifest.json y change_map.json.");
        if (!File.Exists(Path.Combine(dir, "manifest.json")))
            throw new McpToolException($"diff_impact: falta manifest.json en '{cual}' ('{dir}'). Se espera un store .nodes completo.");
        if (!File.Exists(Path.Combine(dir, "change_map.json")))
            throw new McpToolException($"diff_impact: falta change_map.json en '{cual}' ('{dir}'). Se espera un store .nodes completo, generado con --nodestore.");
    }

    private static Dictionary<string, object?> Section(List<string> items, int limit)
    {
        var d = new Dictionary<string, object?> { ["items"] = items.Take(limit).ToList(), ["total"] = items.Count };
        if (items.Count > limit) d["truncated"] = true;
        return d;
    }

    private static List<string> Strings(JsonElement arr) =>
        arr.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    private static List<string> TrimStrings(JsonElement arr, int limit, out bool truncated)
    {
        var total = arr.GetArrayLength();
        truncated = total > limit;
        return arr.EnumerateArray().Take(limit).Select(e => e.GetString() ?? "").ToList();
    }

    private static List<object> ViaCallsAdded(JsonElement arr, int limit, out bool truncated)
    {
        var total = arr.GetArrayLength();
        truncated = total > limit;
        return arr.EnumerateArray().Take(limit).Select(e => (object)new Dictionary<string, object?>
        {
            ["object"] = e.GetProperty("object").GetString(),
            ["depth"] = e.GetProperty("depth").GetInt32(),
            ["conditional"] = e.GetProperty("conditional").GetBoolean(),
        }).ToList();
    }

    private static List<object> ViaDataAdded(JsonElement arr, int limit, out bool truncated)
    {
        var total = arr.GetArrayLength();
        truncated = total > limit;
        return arr.EnumerateArray().Take(limit).Select(e => (object)new Dictionary<string, object?>
        {
            ["table"] = e.GetProperty("table").GetString(),
            ["consumers"] = e.GetProperty("consumers").EnumerateArray().Select(c => c.GetString()).ToList(),
        }).ToList();
    }
}
