using System.Text.Json;

namespace TSqlParser;

/// <summary>
/// Builds change_map.json - the precomputed "what runs from here / whom do I impact"
/// answers, written at the nodestore root next to audit_report.json. Spec: Tarea J
/// (P1-P7) in docs/agent-collab.md, landed by docs/task-change-map.md (which resolves
/// the spec-vs-graph vocabulary drift: hop conditionality comes from the calling
/// Step's condition_path, not from Rule/GOVERNS or CONDITIONED_BY).
///
/// Two top-level sections:
///  - workflows: directed CALLS paths from entry points (in-degree 0 in the
///    procedure/function CALLS subgraph) down to leaves, one path per branch,
///    each hop carrying its conditionality.
///  - impact: per object, the transitive CALLS closure (via_calls, with depth and
///    the condition of the edge that first reaches each callee) and the data fanout
///    (via_data: written table -> objects that read it).
///
/// Steps are always rolled up to their owning object (P6) and never appear in the
/// output. No generated_at of its own - the store-wide timestamp lives in index.json
/// (lesson from the audit_report clock-flaky test).
/// </summary>
public static class ChangeMapExporter
{
    /// <summary>Object types that participate in the workflows/via_calls CALLS subgraph (P5). Triggers react to events (nobody calls them) and are excluded in v1; views/synonyms/scripts don't call.</summary>
    private static readonly HashSet<string> WorkflowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROCEDURE", "SCALAR_FUNCTION", "TABLE_VALUED_FUNCTION",
    };

    /// <summary>Safety valve for pathological CALLS fan-out: paths per entry are capped and the workflow flagged "truncated" (the spec predates this; unbounded branch enumeration is exponential).</summary>
    private const int MaxPathsPerEntry = 200;

    // lineageCache is part of the exporter signature contract (P7, same shape as
    // AuditExporter) though v1 derives everything from the graph itself.
    public static string Generate(
        GraphPayload graph,
        Dictionary<string, (List<string> Roots, int Depth)> lineageCache,
        JsonSerializerOptions jsonOptions)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id, n => n);

        static string Str(Dictionary<string, object> p, string key) =>
            p.TryGetValue(key, out var v) && v is string s ? s : "";

        string PlainName(string id) =>
            byId.TryGetValue(id, out var n)
                ? (Str(n.Properties, "full_name") is { Length: > 0 } fn ? fn : Str(n.Properties, "name") is { Length: > 0 } nm ? nm : id)
                : id.Contains("::") ? id.Split("::", 2)[1] : id;

        // ── Eligible objects + CALLS adjacency (dedup) ───────────────────────
        var eligible = new Dictionary<string, string>(StringComparer.Ordinal); // id -> object_type
        foreach (var n in graph.Nodes)
            if (n.Labels.Contains("SqlObject") && WorkflowTypes.Contains(Str(n.Properties, "object_type")))
                eligible[n.Id] = Str(n.Properties, "object_type").ToUpperInvariant();

        var callsOut = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var callsInCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenCall = new HashSet<(string, string)>();
        foreach (var r in graph.Relationships)
        {
            if (r.Type != "CALLS" || !eligible.ContainsKey(r.StartNodeId) || !eligible.ContainsKey(r.EndNodeId))
                continue;
            if (!seenCall.Add((r.StartNodeId, r.EndNodeId)))
                continue;
            (callsOut.TryGetValue(r.StartNodeId, out var l) ? l : callsOut[r.StartNodeId] = new()).Add(r.EndNodeId);
            // A self-call (direct recursion) must not count as "someone calls me" for
            // entry-point detection below - an object recursing into itself with no
            // external caller is still an entry point of its own workflow.
            if (r.StartNodeId != r.EndNodeId)
                callsInCount[r.EndNodeId] = callsInCount.GetValueOrDefault(r.EndNodeId) + 1;
        }

        // ── Step ownership and step->callee TARGETS (for hop conditionality) ─
        var stepOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in graph.Relationships)
            if (r.Type == "HAS_STEP")
                stepOwner[r.EndNodeId] = r.StartNodeId;

        // (callerId, calleeId) -> condition_paths of every step of caller targeting callee
        var callSteps = new Dictionary<(string, string), List<List<string>>>();
        foreach (var r in graph.Relationships)
        {
            if (r.Type != "TARGETS" || !stepOwner.TryGetValue(r.StartNodeId, out var owner))
                continue;
            var key = (owner, r.EndNodeId);
            var path = new List<string>();
            if (byId.TryGetValue(r.StartNodeId, out var step) &&
                step.Properties.TryGetValue("condition_path", out var cp) &&
                cp is System.Collections.IEnumerable en and not string)
                foreach (var item in en)
                    if (item?.ToString() is { Length: > 0 } entry)
                        path.Add(entry);
            (callSteps.TryGetValue(key, out var lst) ? lst : callSteps[key] = new()).Add(path);
        }

        // A hop is conditional only when EVERY call site is under some condition
        // (any unconditional call site means the callee always runs). The
        // representative condition is the shallowest stack among the sites.
        (bool Conditional, string? Condition, List<string> Stack) HopInfo(string caller, string callee)
        {
            if (!callSteps.TryGetValue((caller, callee), out var paths) || paths.Count == 0)
                return (false, null, new List<string>()); // e.g. FUNCTION calls inside a SELECT: no TARGETS step
            if (paths.Any(p => p.Count == 0))
                return (false, null, new List<string>());
            var rep = paths.OrderBy(p => p.Count).First();
            var last = rep[^1];
            var sep = last.IndexOf(": ", StringComparison.Ordinal);
            var condition = sep >= 0 ? last[(sep + 2)..] : last;
            return (true, condition, rep.Take(rep.Count - 1).ToList());
        }

        // ── workflows (P1, P2, P4) ───────────────────────────────────────────
        var workflows = new List<object>();
        foreach (var entry in eligible.Keys
                     .Where(id => callsInCount.GetValueOrDefault(id) == 0 && callsOut.ContainsKey(id))
                     .OrderBy(PlainName, StringComparer.Ordinal))
        {
            var paths = new List<List<Dictionary<string, object?>>>();
            var truncated = false;

            void Dfs(string node, List<Dictionary<string, object?>> hops, HashSet<string> onPath)
            {
                if (paths.Count >= MaxPathsPerEntry) { truncated = true; return; }
                if (!callsOut.TryGetValue(node, out var callees) || callees.Count == 0)
                {
                    paths.Add(new List<Dictionary<string, object?>>(hops));
                    return;
                }
                foreach (var callee in callees)
                {
                    var (conditional, condition, stack) = HopInfo(node, callee);
                    var hop = new Dictionary<string, object?>
                    {
                        ["from"] = node,
                        ["to"] = callee,
                        ["conditional"] = conditional,
                        ["condition"] = condition,
                        ["condition_stack"] = stack,
                    };
                    if (onPath.Contains(callee))
                    {
                        hop["cycle_back_to"] = callee;
                        var cut = new List<Dictionary<string, object?>>(hops) { hop };
                        if (paths.Count < MaxPathsPerEntry) paths.Add(cut); else truncated = true;
                        continue;
                    }
                    hops.Add(hop);
                    onPath.Add(callee);
                    Dfs(callee, hops, onPath);
                    onPath.Remove(callee);
                    hops.RemoveAt(hops.Count - 1);
                }
            }

            Dfs(entry, new List<Dictionary<string, object?>>(), new HashSet<string>(StringComparer.Ordinal) { entry });

            // Human-oriented one-liner: the first path's chain with conditional marks.
            var description = PlainName(entry);
            if (paths.Count > 0)
                description += string.Concat(paths[0].Select(h =>
                    $" → {PlainName((string)h["to"]!)}{((bool)h["conditional"]! ? " (condicional)" : "")}"));

            var workflow = new Dictionary<string, object?>
            {
                ["entry"] = entry,
                ["entry_name"] = PlainName(entry),
                ["entry_type"] = eligible[entry],
                ["description"] = description,
                ["paths"] = paths.Select(p => new Dictionary<string, object> { ["hops"] = p }).ToList(),
            };
            if (truncated)
                workflow["truncated"] = true;
            workflows.Add(workflow);
        }

        // ── impact.via_calls: transitive CALLS closure per object (P3, P4) ───
        List<Dictionary<string, object?>> ViaCalls(string root)
        {
            var result = new List<Dictionary<string, object?>>();
            var entryOf = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            var onPath = new HashSet<string>(StringComparer.Ordinal) { root };

            void Dfs(string node, int depth)
            {
                if (!callsOut.TryGetValue(node, out var callees))
                    return;
                foreach (var callee in callees)
                {
                    if (callee == root)
                    {
                        // Direct/indirect recursion back to root itself. Root is never
                        // added to entryOf/result by the normal path (it's the DFS seed,
                        // not a "callee"), so without this the whole via_calls closure
                        // would be silently empty for a directly-recursive object even
                        // though it demonstrably calls something (itself). List it once,
                        // flagged as a cycle back to the root.
                        if (entryOf.TryGetValue(callee, out var prior))
                        {
                            prior["cycle_entry"] = true;
                        }
                        else
                        {
                            var (conditionalSelf, conditionSelf, _) = HopInfo(node, callee);
                            var selfEntry = new Dictionary<string, object?>
                            {
                                ["object"] = PlainName(callee),
                                ["depth"] = depth,
                                ["conditional"] = conditionalSelf,
                                ["condition_text"] = conditionSelf,
                                ["cycle_entry"] = true,
                            };
                            entryOf[callee] = selfEntry;
                            result.Add(selfEntry);
                        }
                        continue;
                    }
                    if (onPath.Contains(callee))
                    {
                        // Back-edge: first recurrence of an already-listed node (P4).
                        if (entryOf.TryGetValue(callee, out var prior))
                            prior["cycle_entry"] = true;
                        continue;
                    }
                    if (entryOf.ContainsKey(callee))
                        continue; // already reached by a shorter/earlier path
                    var (conditional, condition, _) = HopInfo(node, callee);
                    var entry = new Dictionary<string, object?>
                    {
                        ["object"] = PlainName(callee),
                        ["depth"] = depth,
                        ["conditional"] = conditional,
                        ["condition_text"] = condition,
                    };
                    entryOf[callee] = entry;
                    result.Add(entry);
                    onPath.Add(callee);
                    Dfs(callee, depth + 1);
                    onPath.Remove(callee);
                }
            }

            Dfs(root, 1);
            return result;
        }

        // ── impact.via_data: written table -> reader objects (P3, P5, P6) ────
        var writesByOwner = new Dictionary<string, List<(string TableId, string Table)>>(StringComparer.Ordinal);
        var readersByTable = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var r in graph.Relationships)
        {
            if (!stepOwner.TryGetValue(r.StartNodeId, out var owner) || !byId.TryGetValue(r.EndNodeId, out var target))
                continue;
            if (!target.Labels.Contains("Table"))
                continue;
            if (r.Type == "WRITES_TO")
            {
                var list = writesByOwner.TryGetValue(owner, out var l) ? l : writesByOwner[owner] = new();
                if (!list.Any(e => e.TableId == r.EndNodeId))
                    list.Add((r.EndNodeId, Str(target.Properties, "name")));
            }
            else if (r.Type == "READS_FROM" && eligible.ContainsKey(owner)) // P5: triggers/views not listed as consumers in v1
            {
                (readersByTable.TryGetValue(r.EndNodeId, out var s) ? s : readersByTable[r.EndNodeId] = new(StringComparer.Ordinal))
                    .Add(owner);
            }
        }

        var impact = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var objId in eligible.Keys.OrderBy(PlainName, StringComparer.Ordinal))
        {
            var viaData = new List<object>();
            foreach (var (tableId, table) in writesByOwner.GetValueOrDefault(objId) ?? new())
            {
                var consumers = (readersByTable.GetValueOrDefault(tableId) ?? new(StringComparer.Ordinal))
                    .Where(o => o != objId)
                    .Select(PlainName)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                viaData.Add(new Dictionary<string, object> { ["table"] = table, ["consumers"] = consumers });
            }

            impact[objId] = new Dictionary<string, object>
            {
                ["name"] = PlainName(objId),
                ["via_calls"] = ViaCalls(objId),
                ["via_data"] = viaData,
            };
        }

        var doc = new Dictionary<string, object>
        {
            ["workflows"] = workflows,
            ["impact"] = impact,
        };
        return JsonSerializer.Serialize(doc, jsonOptions);
    }
}
