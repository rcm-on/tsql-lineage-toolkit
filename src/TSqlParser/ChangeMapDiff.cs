using System.Text;
using System.Text.Json;

namespace TSqlParser;

/// <summary>
/// Diffs two already-generated node stores (a "before" and an "after") into a
/// change_map_diff.json - the substrate of the automated PR comment ("what does
/// this change break?") and the future MCP tool diff_impact. Spec (CLOSED):
/// docs/task-change-map-diff.md.
///
/// Reads ONLY manifest.json (per-object content_hash) and change_map.json
/// (workflows + per-object impact) from each store - no SQL is re-analyzed, by
/// design (spec decision 1: cheap diff of artifacts, not of SQL). Identity is by
/// object id ("Db::Schema.Name"); a rename shows up as removed + added (spec
/// decision 3, no rename detection in v1).
///
/// Output shape and every delta rule are pinned by the spec's "Formato de salida"
/// section - see the inline comments that reference each rule. No timestamps
/// anywhere (lesson from the audit_report clock-flaky test).
/// </summary>
public static class ChangeMapDiff
{
    /// <summary>
    /// In-process entry point (also the CLI's delegate). Returns the process exit
    /// code: 1 if either store is missing manifest.json/change_map.json; with
    /// <paramref name="failOnNewImpact"/>, 2 when the diff surfaces new impact
    /// (see the flag's rule below); otherwise 0. The output file is written in
    /// every success case (both exit 0 and exit 2), never on the exit-1 failure.
    /// </summary>
    public static int Run(string beforeDir, string afterDir, string outPath, bool failOnNewImpact, JsonSerializerOptions jsonOptions)
    {
        // Both required files must exist in both stores - a usage-style failure
        // (stderr + exit 1) with no output written, per spec.
        if (!TryLoad(beforeDir, out var beforeHashes, out var beforeImpact, out var beforeWorkflows, out var err) ||
            !TryLoad(afterDir, out var afterHashes, out var afterImpact, out var afterWorkflows, out err))
        {
            Console.Error.WriteLine(err);
            return 1;
        }

        // ── objects_changed / added / removed (from manifest content_hash) ───
        // content_hash differs => changed; only-in-after => added; only-in-before
        // => removed (spec decision 2).
        var objectsChanged = beforeHashes.Keys
            .Where(id => afterHashes.TryGetValue(id, out var h) && h != beforeHashes[id])
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var objectsAdded = afterHashes.Keys
            .Where(id => !beforeHashes.ContainsKey(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var objectsRemoved = beforeHashes.Keys
            .Where(id => !afterHashes.ContainsKey(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ── impact_delta: only for changed/added objects, only when non-empty ─
        var impactDelta = new Dictionary<string, object>(StringComparer.Ordinal);
        var newlyAffectedUnion = new HashSet<string>(StringComparer.Ordinal); // for summary.newly_affected_total
        var anyViaCallsAdded = false;
        var anyViaDataAdded = false;

        // An object added has no "before" impact, so every reached object/consumer
        // in its "after" impact is newly affected (before-set is empty).
        foreach (var objId in objectsChanged.Concat(objectsAdded).OrderBy(x => x, StringComparer.Ordinal))
        {
            var before = beforeImpact.GetValueOrDefault(objId) ?? ImpactEntry.Empty;
            var after = afterImpact.GetValueOrDefault(objId) ?? ImpactEntry.Empty;

            var viaCallsAdded = ViaCallsDiff(after.ViaCalls, before.ViaCalls);
            var viaCallsRemoved = ViaCallsDiff(before.ViaCalls, after.ViaCalls);
            var viaDataAdded = ViaDataDiff(after.ViaData, before.ViaData);
            var viaDataRemoved = ViaDataDiff(before.ViaData, after.ViaData);

            // newly_affected = (via_calls objects ∪ via_data consumers, after)
            //                − (same set, before). The line that goes to the PR comment.
            var beforeAffected = before.AffectedSet();
            var newlyAffected = after.AffectedSet()
                .Where(name => !beforeAffected.Contains(name))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            // "solo se listan deltas": skip objects whose impact didn't move.
            if (viaCallsAdded.Count == 0 && viaCallsRemoved.Count == 0 &&
                viaDataAdded.Count == 0 && viaDataRemoved.Count == 0 && newlyAffected.Count == 0)
                continue;

            if (viaCallsAdded.Count > 0) anyViaCallsAdded = true;
            if (viaDataAdded.Count > 0) anyViaDataAdded = true;
            foreach (var name in newlyAffected)
                newlyAffectedUnion.Add(name);

            impactDelta[objId] = new Dictionary<string, object>
            {
                ["via_calls_added"] = viaCallsAdded,
                ["via_calls_removed"] = viaCallsRemoved,
                ["via_data_added"] = viaDataAdded,
                ["via_data_removed"] = viaDataRemoved,
                ["newly_affected"] = newlyAffected,
            };
        }

        // ── workflows_delta: matched by entry_name (spec) ────────────────────
        var beforeWf = beforeWorkflows;
        var afterWf = afterWorkflows;
        var workflowsAdded = afterWf.Keys.Where(n => !beforeWf.ContainsKey(n))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var workflowsRemoved = beforeWf.Keys.Where(n => !afterWf.ContainsKey(n))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var workflowsReshaped = new List<object>();
        foreach (var name in beforeWf.Keys.Where(afterWf.ContainsKey).OrderBy(x => x, StringComparer.Ordinal))
        {
            var pathsBefore = beforeWf[name];
            var pathsAfter = afterWf[name];
            if (pathsBefore != pathsAfter)
                workflowsReshaped.Add(new Dictionary<string, object>
                {
                    ["entry"] = name,
                    ["paths_before"] = pathsBefore,
                    ["paths_after"] = pathsAfter,
                });
        }

        // ── summary ──────────────────────────────────────────────────────────
        var newlyAffectedTotal = newlyAffectedUnion.Count;
        // risk_note: null when nothing is newly affected, otherwise a deterministic
        // string over the distinct schema prefixes (the "Schema" of "Db::Schema.X"-
        // derived plain names, i.e. the part before the first '.').
        string? riskNote = null;
        if (newlyAffectedTotal > 0)
        {
            var schemas = newlyAffectedUnion
                .Select(SchemaPrefix)
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            riskNote = "nuevo impacto alcanza: " + string.Join(", ", schemas);
        }

        var doc = new Dictionary<string, object?>
        {
            ["objects_changed"] = objectsChanged,
            ["objects_added"] = objectsAdded,
            ["objects_removed"] = objectsRemoved,
            ["impact_delta"] = impactDelta,
            ["workflows_delta"] = new Dictionary<string, object>
            {
                ["added"] = workflowsAdded,
                ["removed"] = workflowsRemoved,
                ["reshaped"] = workflowsReshaped,
            },
            ["summary"] = new Dictionary<string, object?>
            {
                ["changed"] = objectsChanged.Count,
                ["newly_affected_total"] = newlyAffectedTotal,
                ["risk_note"] = riskNote,
            },
        };

        // Output is written for both the plain run and the --fail-on-new-impact
        // gate (the gate only changes the exit code, not the artifact).
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, jsonOptions), Encoding.UTF8);

        // --fail-on-new-impact: exit 2 on any new impact (new reach, a new call
        // edge, a new write, or a brand-new object); otherwise 0. Plain runs exit 0.
        if (failOnNewImpact && (newlyAffectedTotal > 0 || anyViaCallsAdded || anyViaDataAdded || objectsAdded.Count > 0))
            return 2;
        return 0;
    }

    /// <summary>Entries of <paramref name="left"/> whose (object, depth, conditional)
    /// triple is absent from <paramref name="right"/>, emitted as {object, depth,
    /// conditional} and sorted deterministically.</summary>
    private static List<object> ViaCallsDiff(IReadOnlyList<ViaCall> left, IReadOnlyList<ViaCall> right)
    {
        var rightKeys = new HashSet<string>(right.Select(v => v.Key), StringComparer.Ordinal);
        return left
            .Where(v => !rightKeys.Contains(v.Key))
            .OrderBy(v => v.Object, StringComparer.Ordinal).ThenBy(v => v.Depth).ThenBy(v => v.Conditional)
            .Select(v => (object)new Dictionary<string, object>
            {
                ["object"] = v.Object,
                ["depth"] = v.Depth,
                ["conditional"] = v.Conditional,
            })
            .ToList();
    }

    /// <summary>Per-table consumer delta of <paramref name="left"/> vs
    /// <paramref name="right"/>: a table is emitted if it's newly present in left,
    /// OR it exists in both but left has consumers right lacks (then only those new
    /// consumers are listed). Symmetric for the removed direction (caller swaps args).</summary>
    private static List<object> ViaDataDiff(IReadOnlyList<ViaData> left, IReadOnlyList<ViaData> right)
    {
        var rightByTable = right.ToDictionary(d => d.Table, d => d.Consumers, StringComparer.Ordinal);
        var result = new List<object>();
        foreach (var d in left.OrderBy(d => d.Table, StringComparer.Ordinal))
        {
            List<string> consumers;
            if (!rightByTable.TryGetValue(d.Table, out var rightConsumers))
                consumers = d.Consumers.OrderBy(x => x, StringComparer.Ordinal).ToList(); // newly written table
            else
                consumers = d.Consumers.Where(c => !rightConsumers.Contains(c))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList(); // only the new consumers
            if (consumers.Count == 0)
                continue;
            result.Add(new Dictionary<string, object> { ["table"] = d.Table, ["consumers"] = consumers });
        }
        return result;
    }

    /// <summary>"Db::Schema.Name"-derived plain names are "Schema.Name"; the schema
    /// prefix is the part before the first '.', or the whole name if it has none.</summary>
    private static string SchemaPrefix(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    // ── change_map.json / manifest.json readers ──────────────────────────────

    private static bool TryLoad(
        string dir,
        out Dictionary<string, string> contentHashes,
        out Dictionary<string, ImpactEntry> impact,
        out Dictionary<string, int> workflowPathCounts,
        out string error)
    {
        contentHashes = new(StringComparer.Ordinal);
        impact = new(StringComparer.Ordinal);
        workflowPathCounts = new(StringComparer.Ordinal);
        error = "";

        var manifestPath = Path.Combine(dir, "manifest.json");
        var changeMapPath = Path.Combine(dir, "change_map.json");
        if (!File.Exists(manifestPath))
        {
            error = $"diff-change-map: manifest.json not found in store '{dir}'";
            return false;
        }
        if (!File.Exists(changeMapPath))
        {
            error = $"diff-change-map: change_map.json not found in store '{dir}'";
            return false;
        }

        // manifest.json: objectId -> { content_hash, ... }
        using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)))
        {
            foreach (var prop in manifestDoc.RootElement.EnumerateObject())
                if (prop.Value.TryGetProperty("content_hash", out var h) && h.ValueKind == JsonValueKind.String)
                    contentHashes[prop.Name] = h.GetString()!;
        }

        // change_map.json: impact (objectId -> via_calls/via_data) + workflows.
        using var changeMapDoc = JsonDocument.Parse(File.ReadAllText(changeMapPath, Encoding.UTF8));
        var root = changeMapDoc.RootElement;

        if (root.TryGetProperty("impact", out var impactEl) && impactEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in impactEl.EnumerateObject())
            {
                var viaCalls = new List<ViaCall>();
                if (prop.Value.TryGetProperty("via_calls", out var vc) && vc.ValueKind == JsonValueKind.Array)
                    foreach (var e in vc.EnumerateArray())
                        viaCalls.Add(new ViaCall(
                            e.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "",
                            e.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
                            e.TryGetProperty("conditional", out var c) && c.ValueKind == JsonValueKind.True));

                var viaData = new List<ViaData>();
                if (prop.Value.TryGetProperty("via_data", out var vd) && vd.ValueKind == JsonValueKind.Array)
                    foreach (var e in vd.EnumerateArray())
                    {
                        var consumers = new List<string>();
                        if (e.TryGetProperty("consumers", out var cs) && cs.ValueKind == JsonValueKind.Array)
                            foreach (var c in cs.EnumerateArray())
                                if (c.ValueKind == JsonValueKind.String)
                                    consumers.Add(c.GetString()!);
                        viaData.Add(new ViaData(
                            e.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "",
                            consumers));
                    }

                impact[prop.Name] = new ImpactEntry(viaCalls, viaData);
            }
        }

        // workflows matched by entry_name; we only need the path count to detect
        // reshaping (added/removed come from the name set).
        if (root.TryGetProperty("workflows", out var wfEl) && wfEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var wf in wfEl.EnumerateArray())
            {
                if (!wf.TryGetProperty("entry_name", out var en) || en.ValueKind != JsonValueKind.String)
                    continue;
                var count = wf.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.GetArrayLength() : 0;
                workflowPathCounts[en.GetString()!] = count;
            }
        }

        return true;
    }

    private readonly record struct ViaCall(string Object, int Depth, bool Conditional)
    {
        // Identity for set membership: same callee reached at the same depth and
        // conditionality is "the same" via_call; a depth/conditionality shift shows
        // up as a removed + added pair, which is a real (reportable) delta.
        public string Key => $"{Object} {Depth} {Conditional}";
    }

    private readonly record struct ViaData(string Table, List<string> Consumers);

    private sealed record ImpactEntry(List<ViaCall> ViaCalls, List<ViaData> ViaData)
    {
        public static readonly ImpactEntry Empty = new(new(), new());

        /// <summary>Objects/consumers this entry reaches: via_calls callees ∪
        /// via_data consumers - the set newly_affected is computed over.</summary>
        public HashSet<string> AffectedSet()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var vc in ViaCalls)
                set.Add(vc.Object);
            foreach (var vd in ViaData)
                foreach (var c in vd.Consumers)
                    set.Add(c);
            return set;
        }
    }
}
