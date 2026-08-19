using System.Text.Json;

namespace Parser.Graph;

public static class AuditVerifier
{
    public static int Verify(string nodeStoreDir)
    {
        var auditPath = Path.Combine(nodeStoreDir, "audit_report.json");
        if (!File.Exists(auditPath))
        {
            Console.Error.WriteLine($"[verify-audit] FAIL: {auditPath} not found");
            return 1;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(auditPath)).RootElement;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[verify-audit] FAIL: cannot parse {auditPath}: {ex.Message}");
            return 1;
        }

        var failures = new List<string>();

        // 1. by_type must have no empty key (would indicate object_type property missing)
        if (root.TryGetProperty("summary", out var summary) &&
            summary.TryGetProperty("by_type", out var byType))
        {
            foreach (var prop in byType.EnumerateObject())
            {
                if (prop.Name.Length == 0)
                {
                    failures.Add("summary.by_type contains empty key — object_type property missing on SqlObject nodes");
                    break;
                }
            }
        }

        // 2. lineage_coverage.coverage_pct must be in [0, 100] when there is a
        // surface to measure. When columns_total == 0, coverage_pct is JSON null
        // ("nothing to measure", not "100% covered") and measured is false - skip
        // the range check rather than call GetDouble() on a null element.
        if (root.TryGetProperty("lineage_coverage", out var lc) &&
            lc.TryGetProperty("coverage_pct", out var pct) &&
            pct.ValueKind != JsonValueKind.Null)
        {
            var p = pct.GetDouble();
            if (p < 0 || p > 100)
                failures.Add($"lineage_coverage.coverage_pct out of range: {p}");
        }

        // 3. hotspot entries must have score > 0 and degree > 0
        if (root.TryGetProperty("hotspots", out var hotspots))
        {
            foreach (var h in hotspots.EnumerateArray())
            {
                var name = h.TryGetProperty("name", out var n) ? n.GetString() : "?";
                if (h.TryGetProperty("score", out var score) && score.GetInt32() <= 0)
                    failures.Add($"hotspot '{name}' has score <= 0");
                if (h.TryGetProperty("degree", out var degree) && degree.GetInt32() <= 0)
                    failures.Add($"hotspot '{name}' has degree <= 0");
            }
        }

        // 4. risk_pattern severity values must be one of the known set
        if (root.TryGetProperty("risk_patterns", out var risks))
        {
            var validSev = new HashSet<string>(StringComparer.Ordinal) { "critical", "high", "medium" };
            foreach (var r in risks.EnumerateArray())
            {
                if (r.TryGetProperty("severity", out var sev) && !validSev.Contains(sev.GetString() ?? ""))
                    failures.Add($"risk_pattern has invalid severity: '{sev.GetString()}'");
            }
        }

        // 5. model.json must have a workflows array
        var modelPath = Path.Combine(nodeStoreDir, "model.json");
        if (!File.Exists(modelPath))
        {
            failures.Add($"model.json not found at {modelPath}");
        }
        else
        {
            try
            {
                var model = JsonDocument.Parse(File.ReadAllText(modelPath)).RootElement;
                if (!model.TryGetProperty("workflows", out var wf) || wf.ValueKind != JsonValueKind.Array)
                    failures.Add("model.json missing 'workflows' array");
            }
            catch (JsonException ex)
            {
                failures.Add($"cannot parse model.json: {ex.Message}");
            }
        }

        // 6. audit_report.json must have an impact object
        if (root.TryGetProperty("impact", out var imp) && imp.ValueKind != JsonValueKind.Object)
            failures.Add("audit_report.json 'impact' is not an object");

        // 7. change_map.json (Capa 6) must exist with a workflows array whose
        // entries carry entry/entry_type/paths, and an impact object (P7 of
        // docs/task-change-map.md).
        var changeMapPath = Path.Combine(nodeStoreDir, "change_map.json");
        if (!File.Exists(changeMapPath))
        {
            failures.Add($"change_map.json not found at {changeMapPath}");
        }
        else
        {
            try
            {
                var cm = JsonDocument.Parse(File.ReadAllText(changeMapPath)).RootElement;
                if (!cm.TryGetProperty("workflows", out var cmWf) || cmWf.ValueKind != JsonValueKind.Array)
                {
                    failures.Add("change_map.json missing 'workflows' array");
                }
                else
                {
                    foreach (var w in cmWf.EnumerateArray())
                    {
                        if (!w.TryGetProperty("entry", out _) || !w.TryGetProperty("entry_type", out _) ||
                            !w.TryGetProperty("paths", out var p) || p.ValueKind != JsonValueKind.Array)
                        {
                            failures.Add("change_map.json workflow entry missing entry/entry_type/paths");
                            break;
                        }
                    }
                }
                if (!cm.TryGetProperty("impact", out var cmImp) || cmImp.ValueKind != JsonValueKind.Object)
                    failures.Add("change_map.json missing 'impact' object");
            }
            catch (JsonException ex)
            {
                failures.Add($"cannot parse change_map.json: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"[verify-audit] OK -> {auditPath}");
            return 0;
        }

        Console.Error.WriteLine($"[verify-audit] FAIL ({failures.Count} issue(s)) -> {auditPath}");
        foreach (var f in failures)
            Console.Error.WriteLine($"  • {f}");
        return 1;
    }
}
