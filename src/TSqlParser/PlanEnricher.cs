using System.Text.Json;

namespace TSqlParser;

/// <summary>
/// Merges SQL Server execution plan data into an existing static-analysis graph.
///
/// Noise-free merge strategy:
///   1. For each plan table access, search the FULL proc subgraph (proc node +
///      all its Step nodes) for an existing READS_FROM / WRITES_TO to the same table.
///   2. If found: enrich IN PLACE (add confidence=1.0, actual_rows). No new node/edge.
///   3. If not found: this table was invisible to static analysis (dynamic SQL resolved
///      at runtime, view expanded to base table, linked server, etc.). Only then add a
///      NEW proc-level relationship tagged source="execution_plan" so it is visually
///      distinct from static steps.
///
/// Result: no duplicate edges. Plan data either confirms existing static edges or
/// surfaces truly new runtime-discovered ones.
/// </summary>
public static class PlanEnricher
{
    public record EnrichStats(
        int PlansProcessed,
        int ProcsMatched,
        int RelationshipsConfirmed,   // static edge enriched with plan data
        int RelationshipsDiscovered   // new edge, not visible in static analysis
    );

    public static EnrichStats Enrich(GraphPayload graph, IEnumerable<ExecutionPlanParser.ParsedPlan> plans)
    {
        int plansProcessed = 0, procsMatched = 0, confirmed = 0, discovered = 0;

        // Index: normalized proc name -> SqlObject node
        var procIndex = graph.Nodes
            .Where(n => n.Labels.Contains("SqlObject"))
            .ToDictionary(
                n => NormName(PropStr(n, "full_name")),
                n => n,
                StringComparer.OrdinalIgnoreCase);

        // Index: normalized table signature -> Table node
        var tableIndex = graph.Nodes
            .Where(n => n.Labels.Contains("Table"))
            .ToDictionary(
                n => TableSig(PropStr(n, "database"), PropStr(n, "name")),
                n => n,
                StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
        {
            plansProcessed++;

            foreach (var proc in plan.Procedures)
            {
                if (proc.ProcedureName.Length == 0) continue;

                if (!procIndex.TryGetValue(NormName(proc.ProcedureName), out var procNode))
                    continue;

                procsMatched++;
                var procId = procNode.Id;

                // Collect IDs of every node in this proc's subgraph:
                // the SqlObject itself + all its Steps (via HAS_STEP edges).
                var subgraphIds = graph.Relationships
                    .Where(r => r.Type == "HAS_STEP" && r.StartNodeId == procId)
                    .Select(r => r.EndNodeId)
                    .Append(procId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Build lookup over existing READS_FROM / WRITES_TO in the subgraph.
                // Keyed by (relType, table-signature) so we can match regardless of
                // whether the static edge used a 2-part or 3-part table name.
                var subgraphRels = graph.Relationships
                    .Where(r => r.Type is "READS_FROM" or "WRITES_TO"
                             && subgraphIds.Contains(r.StartNodeId))
                    .ToList();

                var subgraphRelsByKey = subgraphRels
                    .ToLookup(r => (r.Type, TableSig("", PropStr(r.Properties, "table"))));

                // Aggregate actual row stats across all statements in this proc.
                long totalRowsWritten = 0;
                long totalRowsRead    = 0;

                foreach (var stmt in proc.Statements)
                {
                    foreach (var access in stmt.TableAccesses)
                    {
                        var relType = access.IsWrite ? "WRITES_TO" : "READS_FROM";
                        // Cascade: 3-part (db.schema.table) → 2-part (schema.table) → 1-part (table only).
                        // The static analysis often omits the DB prefix for same-DB tables and the schema
                        // prefix for temp tables (#t), so we try progressively looser forms until a match.
                        var sig3    = TableSig(access.Database, $"{access.Schema}.{access.Table}");
                        var sig2    = $"{access.Schema.ToLowerInvariant()}.{access.Table.ToLowerInvariant()}";
                        var sig1    = access.Table.ToLowerInvariant();
                        var matches = subgraphRelsByKey[(relType, sig3)].ToList();
                        if (matches.Count == 0) matches = subgraphRelsByKey[(relType, sig2)].ToList();
                        if (matches.Count == 0) matches = subgraphRelsByKey[(relType, sig1)].ToList();

                        if (access.HasActualRows)
                        {
                            if (access.IsWrite) totalRowsWritten += access.ActualRows;
                            else                totalRowsRead    += access.ActualRows;
                        }

                        if (matches.Count > 0)
                        {
                            // ── CONFIRM ──────────────────────────────────────────────
                            // Static analysis already saw this edge. Enrich in place:
                            // no new node or relationship is added.
                            foreach (var rel in matches)
                            {
                                rel.Properties["confidence"]   = 1.0;
                                rel.Properties["confirmed_by"] = "execution_plan";
                                if (access.HasActualRows)
                                    rel.Properties["actual_rows"] = access.ActualRows;
                            }
                            confirmed++;
                        }
                        else
                        {
                            // ── DISCOVER ─────────────────────────────────────────────
                            // Not in static analysis. Add a PROC-level relationship
                            // clearly tagged as runtime-discovered so it is visually
                            // distinct from static Step edges (no duplicate of step rels).
                            var tableNode = GetOrCreateTableNode(graph, tableIndex, access);
                            var newRel = new GraphRel
                            {
                                Type        = relType,
                                StartNodeId = procId,
                                EndNodeId   = tableNode.Id,
                                Properties  = new Dictionary<string, object>
                                {
                                    ["table"]           = access.FullName,
                                    ["action_type"]     = access.IsWrite ? "WRITE" : "READ",
                                    ["confidence"]      = 1.0,
                                    ["source"]          = "execution_plan",
                                    ["operation"]       = access.Operation,
                                }
                            };
                            if (access.HasActualRows)
                                newRel.Properties["actual_rows"] = access.ActualRows;

                            graph.Relationships.Add(newRel);

                            // Also register in the lookup so a second plan file
                            // referencing the same table doesn't add it again.
                            subgraphRelsByKey = subgraphRels
                                .Append(newRel)
                                .ToLookup(r => (r.Type, TableSig("", PropStr(r.Properties, "table"))));

                            discovered++;
                        }
                    }
                }

                // Enrich the Process node with aggregate runtime stats (actual plans only).
                if (plan.IsActualPlan)
                {
                    procNode.Properties["actual_rows_written"] = totalRowsWritten;
                    procNode.Properties["actual_rows_read"]    = totalRowsRead;
                    procNode.Properties["plan_source"]         = plan.FileName;
                }
            }
        }

        // Re-assign relationship IDs after adding new ones.
        for (int i = 0; i < graph.Relationships.Count; i++)
            graph.Relationships[i].Id = $"r{i}";

        return new EnrichStats(plansProcessed, procsMatched, confirmed, discovered);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static GraphNode GetOrCreateTableNode(
        GraphPayload graph,
        Dictionary<string, GraphNode> tableIndex,
        ExecutionPlanParser.PlanTableAccess access)
    {
        var sig = TableSig(access.Database, $"{access.Schema}.{access.Table}");
        if (tableIndex.TryGetValue(sig, out var existing))
            return existing;

        var id = $"{access.Database}:table:{access.Schema}.{access.Table}".ToLowerInvariant();
        var node = new GraphNode
        {
            Id     = id,
            Labels = ["Table"],
            Properties = new Dictionary<string, object>
            {
                ["database"] = access.Database,
                ["schema"]   = access.Schema,
                ["name"]     = $"{access.Schema}.{access.Table}",
                ["source"]   = "execution_plan",
            }
        };
        graph.Nodes.Add(node);
        tableIndex[sig] = node;
        return node;
    }

    /// <summary>
    /// Canonical table signature for fuzzy matching between static analysis
    /// (may omit the DB prefix for same-DB tables; may omit schema for temp tables)
    /// and plan data (always has db.schema.table).
    ///
    /// Rules:
    ///   fullName already 3-part (db.schema.table) → strip and return as-is.
    ///   fullName 2-part (schema.table) + db given  → prepend db → 3-part sig.
    ///   fullName 2-part (schema.table) + no db     → return 2-part as-is.
    ///   fullName 1-part (table only)               → return 1-part as-is.
    /// </summary>
    private static string TableSig(string db, string fullName)
    {
        var name = fullName.Trim().Replace("[", "").Replace("]", "").ToLowerInvariant();
        var parts = name.Split('.');
        return parts.Length switch
        {
            >= 3 => $"{parts[^3]}.{parts[^2]}.{parts[^1]}",
            2    => db.Length > 0 ? $"{db.ToLowerInvariant()}.{name}" : name,
            _    => name,
        };
    }

    private static string NormName(string s) =>
        s.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();

    private static string PropStr(GraphNode n, string key) =>
        PropStr(n.Properties, key);

    private static string PropStr(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return "";
        // After JsonSerializer.Deserialize, values are JsonElement objects.
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String
                ? je.GetString() ?? ""
                : je.ToString();
        return v.ToString() ?? "";
    }
}
