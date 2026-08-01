using System.Text.Json.Serialization;

namespace Parser.Contracts;

public class GraphNode
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("labels")] public required List<string> Labels { get; init; }
    [JsonPropertyName("properties")] public required Dictionary<string, object> Properties { get; init; }
}

public class GraphRel
{
    /// <summary>Stable unique id assigned at the end of GraphExporter.Build (e.g. "r0", "r1", ...).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public required string Type { get; init; }
    /// <summary>Id of the start (source) node.</summary>
    [JsonPropertyName("source")] public required string StartNodeId { get; init; }
    /// <summary>Id of the end (target) node.</summary>
    [JsonPropertyName("target")] public required string EndNodeId { get; init; }
    [JsonPropertyName("properties")] public Dictionary<string, object> Properties { get; init; } = new();
}

public class GraphPayload
{
    // init (not just get) so JsonSerializer.Deserialize can populate these lists
    // when re-reading a previously serialized graph (e.g. enrich-from-plans).
    [JsonPropertyName("nodes")] public List<GraphNode> Nodes { get; init; } = new();
    [JsonPropertyName("relationships")] public List<GraphRel> Relationships { get; init; } = new();
}
