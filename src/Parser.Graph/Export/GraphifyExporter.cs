using System.Text.Json.Serialization;

namespace Parser.Graph;

/// <summary>
/// Converts the Neo4j-shaped GraphPayload ({ Nodes, Relationships }) into the
/// flat { meta, stats, nodes, edges } shape that src/exporter.py emits for
/// Graphify / D3.js / Gephi. Graphify renders nodes by reading top-level fields
/// (id/label/type/size/color) and edges by source/target/type/color/dashed, so -
/// unlike the Neo4j export - every node property is FLATTENED to the top level
/// and each node/edge gets a size+color derived from its type. Without size/color
/// Graphify draws zero-sized, uncolored nodes (i.e. "no output"), which is why the
/// nested-properties shape didn't render.
/// </summary>
public static class GraphifyExporter
{
    // Distinct color + base size per node type, so the graph is legible at a glance:
    // SqlObjects largest, then Tables, then the finer-grained Column/Step/Rule nodes.
    private static readonly Dictionary<string, (string Color, double Size)> TypeStyle = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SqlObject"] = ("#e91e8c", 20),
        ["Table"] = ("#4caf50", 16),
        ["Column"] = ("#2196f3", 9),
        ["Step"] = ("#ff9800", 12),
        ["Action"] = ("#9c27b0", 11),
        ["Parameter"] = ("#00bcd4", 10),
        ["Variable"] = ("#cddc39", 10),
        ["IF"] = ("#795548", 11),
        ["IFELSE"] = ("#795548", 11),
        ["WHILE"] = ("#795548", 11),
        ["CATCH"] = ("#b71c1c", 11),
    };

    private const string DefaultColor = "#777777";
    private const double DefaultSize = 10;

    public static GraphifyPayload ToGraphify(GraphPayload graph, string database)
    {
        var nodes = graph.Nodes.Select(BuildNode).ToList();
        var edges = graph.Relationships.Select(r => new Dictionary<string, object>
        {
            ["source"] = r.StartNodeId,
            ["target"] = r.EndNodeId,
            ["type"] = r.Type,
            ["label"] = r.Type,
            ["color"] = "#666666",
            ["dashed"] = r.Type is "AFFECTS" or "BUILDS_SQL_FROM" or "DERIVES_FROM" or "ASSIGNED_FROM",
            ["properties"] = r.Properties,
        }).ToList();

        var byType = nodes.GroupBy(n => (string)n["type"]).ToDictionary(g => g.Key, g => (object)g.Count());
        var byEdgeType = edges.GroupBy(e => (string)e["type"]).ToDictionary(g => g.Key, g => (object)g.Count());

        return new GraphifyPayload
        {
            Meta = new Dictionary<string, object>
            {
                ["database"] = database,
                ["generated_at"] = DateTime.Now.ToString("o"),
                ["tool"] = "sql-analyzer/tsql-parser",
                ["format"] = "graphify-v1",
            },
            Stats = new Dictionary<string, object>
            {
                ["total_nodes"] = nodes.Count,
                ["total_edges"] = edges.Count,
                ["nodes_by_type"] = byType,
                ["edges_by_type"] = byEdgeType,
            },
            Nodes = nodes,
            Edges = edges,
        };
    }

    private static Dictionary<string, object> BuildNode(GraphNode n)
    {
        var type = n.Labels.Count > 0 ? n.Labels[^1] : "Node";
        var (color, size) = TypeStyle.TryGetValue(type, out var style) ? style : (DefaultColor, DefaultSize);

        var node = new Dictionary<string, object>
        {
            ["id"] = n.Id,
            ["label"] = DisplayLabel(n),
            ["type"] = type,
            ["labels"] = n.Labels,
            ["size"] = size,
            ["color"] = color,
        };

        // Flatten every property to the top level (Graphify reads them there), but
        // never clobber the identity/visual keys set above.
        foreach (var (key, val) in n.Properties)
            if (!node.ContainsKey(key))
                node[key] = val;

        return node;
    }

    private static string DisplayLabel(GraphNode n)
    {
        // "expression" carries the business rule itself (e.g. "@Status = 'Closed'")
        // for Rule nodes; "label"/"full_name"/"name" cover Steps/SqlObjects/Tables/
        // Columns. Whichever exists first is the human-readable caption Graphify shows.
        foreach (var key in new[] { "label", "full_name", "name", "expression" })
            if (n.Properties.TryGetValue(key, out var val) && val is string s && s.Length > 0)
                return s;
        return n.Id;
    }

    // Nodes/edges are plain property bags (heterogeneous flat fields), so they
    // serialize as free-form JSON objects - matching src/exporter.py's dict output.
    public class GraphifyPayload
    {
        [JsonPropertyName("meta")] public required Dictionary<string, object> Meta { get; init; }
        [JsonPropertyName("stats")] public required Dictionary<string, object> Stats { get; init; }
        [JsonPropertyName("nodes")] public required List<Dictionary<string, object>> Nodes { get; init; }
        [JsonPropertyName("edges")] public required List<Dictionary<string, object>> Edges { get; init; }
    }
}
