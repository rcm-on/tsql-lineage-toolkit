using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

/// <summary>Signals a rejected/failed tool call (bad arguments, unknown id) - never an
/// uncaught exception, always surfaced by the transport as a normal tools/call result
/// with isError:true.</summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

/// <summary>
/// Pure query handlers behind the two MCP tools: (open read-only SqliteConnection,
/// arguments) -&gt; plain Dictionary response, independent of stdio/JSON-RPC so they're
/// testable without a process. Queries the graph_full.db schema written by
/// SqliteExporter: nodes(id,label,name,...), edges(src,dst,type,action_type,props).
/// Every response stays deliberately thin (ids/names only, no props) to fit an
/// agent's context budget - see ResponseBudgetBytes.
/// </summary>
public static class McpTools
{
    /// <summary>Hard target for a serialized response at default limits (see McpTests
    /// BudgetGateTests) - this is a design gate, not a soft guideline.</summary>
    public const int ResponseBudgetBytes = 2048;

    private static readonly string[] ImpactEdgeTypes =
        ["CALLS", "READS_FROM", "WRITES_TO", "DERIVES_FROM", "READS_COLUMN", "WRITES_COLUMN"];
    private static readonly string ImpactEdgeTypesLabel = string.Join("/", ImpactEdgeTypes);

    // Defensive ceiling on BFS size so a pathological depth/limit combination on a huge
    // graph can't turn one tool call into an unbounded scan; independent of `limit`.
    private const int SafetyCap = 5000;
    private const int SqliteInBatchSize = 500;

    public static Dictionary<string, object?> ResolveObject(SqliteConnection conn, string name, int limit)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpToolException("resolve_object: 'name' no puede estar vacío.");
        if (limit <= 0) limit = 10;
        limit = Math.Min(limit, 200);

        var needle = name.Trim();
        var needleLower = needle.ToLowerInvariant();
        var candidates = new List<(string Id, string Label, string Name, int Score)>();

        using (var cmd = conn.CreateCommand())
        {
            // Only the entities an agent could plausibly mean by a loose name - Step/
            // Action/Variable/Rule/etc. are graph-internal plumbing, not addressable objects.
            cmd.CommandText =
                "SELECT id, label, name FROM nodes " +
                "WHERE label IN ('SqlObject','Table','Column') " +
                "  AND (lower(name) LIKE '%' || $n || '%' ESCAPE '\\' " +
                "       OR lower(id) LIKE '%' || $n || '%' ESCAPE '\\')";
            cmd.Parameters.AddWithValue("$n", EscapeLike(needleLower));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var nodeName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var score = MatchScore(id, nodeName, needleLower);
                if (score > 0)
                    candidates.Add((id, label, nodeName, score));
            }
        }

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = ordered.Count;
        var exactCount = ordered.Count(c => c.Score == 3);
        var page = ordered.Take(limit).ToList();

        var result = new Dictionary<string, object?>
        {
            ["matches"] = page.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["label"] = c.Label,
                ["name"] = c.Name,
            }).ToList(),
            ["total"] = total,
        };
        if (total > limit) result["truncated"] = true;
        if (exactCount == 1) result["exact"] = true;
        return result;
    }

    public static Dictionary<string, object?> Impact(SqliteConnection conn, string id, string direction, int depth, int limit)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpToolException("impact: 'id' no puede estar vacío.");
        direction = string.IsNullOrWhiteSpace(direction) ? "downstream" : direction.Trim().ToLowerInvariant();
        if (direction is not ("downstream" or "upstream"))
            throw new McpToolException($"impact: direction debe ser 'downstream' o 'upstream', no '{direction}'.");
        if (depth < 1 || depth > 5)
            throw new McpToolException("impact: depth debe estar entre 1 y 5.");
        if (limit <= 0) limit = 50;
        limit = Math.Min(limit, 500);

        if (!NodeExists(conn, id))
            throw new McpToolException($"impact: no existe ningún nodo con id '{id}'.");

        // Edges point actor -> resource-acted-upon (caller->callee, reader->table,
        // writer->table, derived-column->source-column). "downstream" (what breaks if
        // `id` changes) walks edges backwards (dst==frontier, collect src); "upstream"
        // (what `id` depends on) walks them forwards (src==frontier, collect dst).
        var matchColumn = direction == "downstream" ? "dst" : "src";
        var otherColumn = direction == "downstream" ? "src" : "dst";

        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontier = new List<string> { id };
        var affected = new List<(string Id, int Hops)>();

        for (var hop = 1; hop <= depth && frontier.Count > 0 && affected.Count < SafetyCap; hop++)
        {
            var next = new List<string>();
            foreach (var otherId in QueryNeighbors(conn, frontier, matchColumn, otherColumn))
            {
                if (!visited.Add(otherId)) continue;
                next.Add(otherId);
                affected.Add((otherId, hop));
            }
            frontier = next;
        }

        var total = affected.Count;
        var page = affected.Take(limit).ToList();
        var info = LookupNodes(conn, page.Select(p => p.Id));

        var items = page.Select(p =>
        {
            var (label, nodeName) = info.TryGetValue(p.Id, out var v) ? v : ("", p.Id);
            return new Dictionary<string, object?>
            {
                ["id"] = p.Id,
                ["name"] = nodeName,
                ["label"] = label,
                ["hops"] = p.Hops,
            };
        }).ToList();

        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["direction"] = direction,
            ["depth"] = depth,
            ["affected"] = items,
            ["total"] = total,
        };
        if (total > limit) result["truncated"] = true;

        // An empty "affected" reads as a fact ("nothing depends on this") unless it
        // carries its own explanation - distinguish "no such edges at all" from "the
        // only ones found loop back to the node itself" (self-recursion; depth can't
        // fix that), and check whether the other direction would have answered instead.
        if (total == 0)
        {
            var hasRawEdge = HasAnyEdge(conn, id, matchColumn);
            result["reason"] = hasRawEdge
                ? "las únicas aristas encontradas vuelven al propio nodo (auto-referencia); depth no lo cambia."
                : direction == "downstream"
                    ? $"sin aristas {ImpactEdgeTypesLabel} entrantes a este nodo."
                    : $"sin aristas {ImpactEdgeTypesLabel} salientes de este nodo.";

            var oppositeDirection = direction == "downstream" ? "upstream" : "downstream";
            var oppositeMatch = otherColumn;
            var oppositeOther = matchColumn;
            var oppositeCount = QueryNeighbors(conn, [id], oppositeMatch, oppositeOther).Count(x => x != id);
            if (oppositeCount > 0)
                result["hint"] = $"0 en {direction}, pero {oppositeDirection} tiene {oppositeCount} objeto(s) a 1 salto - prueba direction={oppositeDirection}.";
        }

        return result;
    }

    private static bool HasAnyEdge(SqliteConnection conn, string id, string matchColumn)
    {
        using var cmd = conn.CreateCommand();
        var typesCsv = string.Join(",", ImpactEdgeTypes.Select(t => $"'{t}'"));
        var whereMatch = matchColumn == "src" ? "(src = $id OR src LIKE $id || '#%')" : $"{matchColumn} = $id";
        cmd.CommandText = $"SELECT 1 FROM edges WHERE {whereMatch} AND type IN ({typesCsv}) LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static IEnumerable<string> QueryNeighbors(SqliteConnection conn, List<string> ids, string matchColumn, string otherColumn)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var typesCsv = string.Join(",", ImpactEdgeTypes.Select(t => $"'{t}'"));
        for (var offset = 0; offset < ids.Count; offset += SqliteInBatchSize)
        {
            var batch = ids.Skip(offset).Take(SqliteInBatchSize).ToList();
            using var cmd = conn.CreateCommand();
            string whereMatch;
            if (matchColumn == "src")
            {
                // edges.src is Step-granular for READS_FROM/WRITES_TO/READS_COLUMN/
                // WRITES_COLUMN (a Step id is "<objId>#stepN"); CALLS is the only type
                // with a SqlObject directly on src. Match the object id itself or any
                // step owned by it.
                whereMatch = string.Join(" OR ", batch.Select((_, i) => $"(src = $p{i} OR src LIKE $p{i} || '#%')"));
            }
            else
            {
                whereMatch = $"{matchColumn} IN ({string.Join(",", batch.Select((_, i) => $"$p{i}"))})";
            }
            cmd.CommandText = $"SELECT DISTINCT {otherColumn} FROM edges WHERE ({whereMatch}) AND type IN ({typesCsv})";
            for (var i = 0; i < batch.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", batch[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetString(0);
                var v = otherColumn == "src" ? RollUpStep(raw) : raw;
                if (seen.Add(v))
                    yield return v;
            }
        }
    }

    /// <summary>A Step id ("&lt;objId&gt;#stepN") rolled up to its owning SqlObject; any
    /// other id (Table/Column/SqlObject) passes through unchanged.</summary>
    private static string RollUpStep(string id)
    {
        var hash = id.IndexOf('#');
        return hash > 0 ? id[..hash] : id;
    }

    private static Dictionary<string, (string Label, string Name)> LookupNodes(SqliteConnection conn, IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (var offset = 0; offset < idList.Count; offset += SqliteInBatchSize)
        {
            var batch = idList.Skip(offset).Take(SqliteInBatchSize).ToList();
            using var cmd = conn.CreateCommand();
            var placeholders = string.Join(",", batch.Select((_, i) => $"$p{i}"));
            cmd.CommandText = $"SELECT id, label, name FROM nodes WHERE id IN ({placeholders})";
            for (var i = 0; i < batch.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", batch[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
        }
        return result;
    }

    private static bool NodeExists(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM nodes WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    // 3 = exact (name or id); 2 = suffix at a "." or ":" boundary (e.g. needle
    // "orderlines" against id "...:table:sales.orderlines"); 1 = plain substring.
    private static int MatchScore(string id, string name, string needleLower)
    {
        var idLower = id.ToLowerInvariant();
        var nameLower = name.ToLowerInvariant();
        if (nameLower == needleLower || idLower == needleLower) return 3;
        if (EndsAtBoundary(nameLower, needleLower) || EndsAtBoundary(idLower, needleLower)) return 2;
        if (nameLower.Contains(needleLower) || idLower.Contains(needleLower)) return 1;
        return 0;
    }

    private static bool EndsAtBoundary(string haystack, string needle)
    {
        if (!haystack.EndsWith(needle, StringComparison.Ordinal)) return false;
        var i = haystack.Length - needle.Length;
        return i == 0 || haystack[i - 1] is '.' or ':';
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
