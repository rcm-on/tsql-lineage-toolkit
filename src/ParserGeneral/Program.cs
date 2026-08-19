// ParserGeneral: orchestrator CLI that fuses the SQL-side graph (TSqlParser,
// via TSqlExtractorAdapter) and the app-side graph (NetParser, via NetExtractor)
// into a single graph_full.json (and optionally a unified NodeStore), so
// downstream tooling (change_map, lineage-queries, dashboard) sees one graph
// spanning "app method -> proc -> table -> consumers". See
// docs/task-app-bridge.md.
//
// Usage:
//   parsergeneral <input1> [input2 ...] --out <dir> [--nodestore] [--columns]
//
// Each <input> is routed to the first registered IGraphExtractor whose
// CanHandle(input) is true:
//   - TSqlExtractorAdapter: *.json (input.json), *.sql files/directories.
//   - NetExtractor:         *.sln, *.csproj, directories containing either.
//
// Resulting GraphPayloads are merged: nodes deduplicated by Id (SQL-side
// "Db::..." and app-side "app::..." ids never collide by construction; first
// occurrence wins, Labels are unioned), relationships concatenated and
// reassigned stable ids ("r0", "r1", ...) the same way GraphExporter.Build does.

using System.Text;
using System.Text.Json;
using NetParser;
using Parser.Contracts;
using Parser.Graph;
using ParserGeneral;
using TSqlParser;

var positional = new List<string>();
string? outDir = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--out")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("--out requires a directory argument.");
            return 1;
        }
        outDir = args[++i];
    }
    else if (!args[i].StartsWith("--"))
    {
        positional.Add(args[i]);
    }
}

var includeColumns = args.Contains("--columns");
var emitNodeStore = args.Contains("--nodestore");
var allowPartial = args.Contains("--allow-partial");

if (positional.Count == 0 || outDir == null)
{
    Console.Error.WriteLine(
        "Usage: parsergeneral <input1> [input2 ...] --out <dir> [--nodestore] [--columns] [--allow-partial]");
    return 1;
}

var tsqlExtractor = new TSqlExtractorAdapter();
var netExtractor = new NetExtractor { AllowPartial = allowPartial };
IGraphExtractor[] registry = { tsqlExtractor, netExtractor };

var payloads = new List<GraphPayload>();
foreach (var input in positional)
{
    var extractor = registry.FirstOrDefault(e => e.CanHandle(input));
    if (extractor == null)
    {
        Console.Error.WriteLine($"No extractor can handle '{input}' - skipped.");
        continue;
    }

    GraphPayload payload;
    try
    {
        payload = extractor is TSqlExtractorAdapter tsql
            ? tsql.Extract(input, includeColumns)
            : extractor.Extract(input);
    }
    catch (UnsupportedProjectException ex)
    {
        // Nothing is written: a graph missing a whole project would answer impact
        // questions with silent gaps, and the caller has no way to tell.
        Console.Error.WriteLine($"[{extractor.Name}] {input}:");
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    Console.WriteLine($"[{extractor.Name}] {input}: {payload.Nodes.Count} nodes, {payload.Relationships.Count} relationships");
    if (allowPartial)
    {
        var skipped = payload.Nodes
            .Where(n => n.Properties.TryGetValue("analyzed", out var a) && a is false)
            .ToList();
        foreach (var node in skipped)
            Console.WriteLine($"  WARNING not analysed: {node.Properties["name"]} - {node.Properties["unsupported_reason"]}");
        if (skipped.Count > 0)
            Console.WriteLine($"  Graph is PARTIAL: {skipped.Count} project(s) excluded. Impact answers may be incomplete.");
    }
    payloads.Add(payload);
}

if (payloads.Count == 0)
{
    Console.Error.WriteLine("No input was handled by any extractor; nothing to write.");
    return 1;
}

var merged = Merge(payloads);

Directory.CreateDirectory(outDir);
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var graphOutputPath = Path.Combine(outDir, "graph_full.json");
File.WriteAllText(graphOutputPath, JsonSerializer.Serialize(merged, jsonOptions), Encoding.UTF8);
Console.WriteLine($"Graph: {merged.Nodes.Count} nodes, {merged.Relationships.Count} relationships -> {graphOutputPath}");

if (emitNodeStore)
{
    var db = InferDatabaseName(merged);
    var nodeStorePath = Path.Combine(outDir, "graph_full.nodes");
    var stats = NodeStoreExporter.Write(merged, nodeStorePath, db, jsonOptions);
    Console.WriteLine($"NodeStore: {stats.Objects} objects, {stats.SharedNodes} shared nodes, {stats.Edges} edges -> {nodeStorePath}");
}

return 0;

// Merges several partial GraphPayloads into one: nodes deduplicated by Id
// (first occurrence wins, Labels unioned across duplicates), relationships
// concatenated and reassigned stable "r<i>" ids in payload order - the same
// convention GraphExporter.Build uses, so downstream tooling (change_map,
// NodeStoreExporter, lineage-queries) sees ids in the shape it already expects.
static GraphPayload Merge(List<GraphPayload> payloads)
{
    var nodesById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
    var nodeOrder = new List<string>();

    foreach (var payload in payloads)
    {
        foreach (var node in payload.Nodes)
        {
            if (nodesById.TryGetValue(node.Id, out var existing))
            {
                var unionLabels = existing.Labels.Union(node.Labels, StringComparer.Ordinal).ToList();
                if (unionLabels.Count != existing.Labels.Count)
                {
                    existing.Labels.Clear();
                    existing.Labels.AddRange(unionLabels);
                }
            }
            else
            {
                nodesById[node.Id] = node;
                nodeOrder.Add(node.Id);
            }
        }
    }

    var relationships = new List<GraphRel>();
    foreach (var payload in payloads)
        relationships.AddRange(payload.Relationships);
    for (var i = 0; i < relationships.Count; i++)
        relationships[i].Id = $"r{i}";

    return new GraphPayload
    {
        Nodes = nodeOrder.Select(id => nodesById[id]).ToList(),
        Relationships = relationships,
    };
}

// Best-effort database name for NodeStoreExporter's index.json metadata: the
// prefix before "::" of the first SqlObject-shaped id found, "merged" if the
// graph is app-only (no SQL side).
static string InferDatabaseName(GraphPayload graph)
{
    foreach (var node in graph.Nodes)
    {
        var parts = node.Id.Split("::", 2);
        if (parts.Length == 2 && !node.Id.StartsWith("app::", StringComparison.Ordinal))
            return parts[0];
    }
    return "merged";
}
