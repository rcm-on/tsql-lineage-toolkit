using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSqlParser;

/// <summary>
/// Writes the in-memory <see cref="GraphPayload"/> (the same nodes/relationships
/// that GraphExporter.Build produces) as a navigable, incremental node store on
/// disk - the opposite trade-off to the monolithic graph_full.json. An agent
/// answering "what writes to dbo.T1?" never loads the whole graph: it reads
/// model.json (the macro view: SqlObjects + Tables, with object-level edges),
/// jumps to the table's shared file, and reads its edges_in - following only the
/// files it actually needs.
///
/// The store is partitioned by *owner* so a future "only re-write what changed"
/// pass has a natural unit of work:
///
///   - Owned nodes (id has the form "&lt;ObjectId&gt;#..." or is itself an
///     ObjectId): SqlObject + its Parameters/Variables/Steps. Created and
///     destroyed by exactly one SqlObject. Embedded together in one file:
///     objects/&lt;slug&gt;/object.json.
///   - Shared nodes (no object prefix): Table/Column/Action/Rule, deduplicated
///     across objects. One file each under shared/&lt;category&gt;/, with incoming
///     edges partitioned per contributing object ("refs") so a single object's
///     contribution can be replaced without touching the rest.
///
/// Layout:
///   &lt;out&gt;/
///     index.json            meta + closed schema + stats + howto + entry points
///     model.json             macro graph: SqlObject/Table nodes, CALLS/AFFECTS/
///                            FK_TO edges, plus WRITES_TO/READS_FROM rolled up from
///                            Step level to the owning SqlObject - the "initial
///                            nodes" an agent loads first to decide where to dig.
///     manifest.json          per-object content hash + file + shared nodes it
///                            contributed to - the bookkeeping <see cref="Update"/>
///                            uses to limit rewrites to what actually changed.
///     objects/&lt;slug&gt;/object.json   one SqlObject + its owned nodes + edges_out
///     shared/&lt;category&gt;/&lt;slug&gt;.json  one shared node + edges_in (by owner) + edges_out
/// </summary>
public static class NodeStoreExporter
{
    // Closed vocabularies, mirrored from GraphExporter. Emitted into index.json
    // as the store's contract; anything outside them is flagged in stats rather
    // than silently dropped, so a reader can rely on the type set.
    public static readonly IReadOnlyList<string> KnownNodeLabels = new[]
    {
        "SqlObject", "Process", "Workflow", "Parameter", "Variable", "Step", "Action", "Table", "Column", "Rule",
    };

    public static readonly IReadOnlyList<string> KnownEdgeTypes = new[]
    {
        "HAS_PARAMETER", "DECLARES", "ASSIGNED_FROM", "HAS_STEP", "ACTION", "BUILDS_SQL_FROM",
        "USES_VARIABLE", "TARGETS", "WRITES_TO", "READS_FROM", "READS_COLUMN", "WRITES_COLUMN",
        "FILTERS_ON", "DERIVES_FROM", "CONDITIONED_BY", "NESTED_IN", "GOVERNS", "CALLS", "AFFECTS", "HAS_COLUMN", "FK_TO", "REFERENCES",
        "BELONGS_TO", "WORKFLOW_WRITES_TO",
    };

    // Structural edges fully represented by an object's "owned" lists already
    // (HAS_PARAMETER/DECLARES/HAS_STEP just say "this SqlObject has this Parameter/
    // Variable/Step", which `owned.*` already encodes) - redundant in edges_out.
    private static readonly HashSet<string> StructuralEdgeTypes = new(StringComparer.Ordinal)
    {
        "HAS_PARAMETER", "DECLARES", "HAS_STEP",
    };

    // The only edge types worth following to *navigate between objects/tables*:
    // a call chain, a write/read target, an impact chain, a foreign key. Every
    // other edge an object owns (ACTION, BUILDS_SQL_FROM, USES_VARIABLE, GOVERNS,
    // TARGETS...) is intra-object plumbing that bloats object.json without helping
    // an agent decide its next hop. nav.json keeps only these so a multi-object
    // traversal reads a ~2-4 KB file per hop instead of the full ~60 KB object.
    private static readonly HashSet<string> NavEdgeTypes = new(StringComparer.Ordinal)
    {
        "CALLS", "WRITES_TO", "READS_FROM", "AFFECTS", "FK_TO",
    };

    public class Stats
    {
        public int Nodes { get; init; }
        public int Edges { get; init; }
        public int Objects { get; init; }
        public int SharedNodes { get; init; }
        public int OrphanEdges { get; init; }
        public Dictionary<string, int> NodesByLabel { get; init; } = new();
        public Dictionary<string, int> EdgesByType { get; init; } = new();
        public List<string> UnknownLabels { get; init; } = new();
        public List<string> UnknownEdgeTypes { get; init; } = new();
    }

    /// <summary>Result of <see cref="Update"/>: <see cref="Stats"/> plus how many
    /// objects/shared files were actually written vs. left untouched vs. removed.</summary>
    public class UpdateStats : Stats
    {
        public int ObjectsWritten { get; init; }
        public int ObjectsUnchanged { get; init; }
        public int ObjectsRemoved { get; init; }
        public int SharedWritten { get; init; }
        public int SharedUnchanged { get; init; }
        public int SharedRemoved { get; init; }
    }

    /// <summary>In-memory result of <see cref="Build"/>: every file the store
    /// consists of, serialized but not yet written to disk.</summary>
    private class BuildResult
    {
        public required Dictionary<string, string> ObjectFiles { get; init; }
        public required Dictionary<string, string> SharedFiles { get; init; }
        public required string ModelJson { get; init; }
        public required string ManifestJson { get; init; }
        public required string IndexJson { get; init; }
        public required Stats Stats { get; init; }
    }

    private class ManifestEntry
    {
        [JsonPropertyName("content_hash")]
        public string ContentHash { get; set; } = "";

        [JsonPropertyName("object_file")]
        public string ObjectFile { get; set; } = "";

        [JsonPropertyName("nav_file")]
        public string NavFile { get; set; } = "";

        [JsonPropertyName("shared_touched")]
        public List<string> SharedTouched { get; set; } = new();
    }

    /// <summary>
    /// Materializes <paramref name="graph"/> into a node store under
    /// <paramref name="outDir"/> (cleared and fully rewritten). Returns counts plus
    /// any integrity problems found (edges pointing at missing nodes, types outside
    /// the closed vocabularies) - none of which abort the write; they are
    /// reported so the upstream graph can be tightened.
    /// </summary>
    public static Stats Write(GraphPayload graph, string outDir, string database, JsonSerializerOptions jsonOptions)
    {
        var build = Build(graph, database, jsonOptions);
        PrepareDir(outDir);
        WriteAll(outDir, build);
        return build.Stats;
    }

    /// <summary>
    /// Like <see cref="Write"/>, but <paramref name="outDir"/> is an existing store
    /// (or doesn't exist yet). Only writes <c>objects/**</c>/<c>shared/**</c> files
    /// whose content actually changed, deletes files for objects/shared nodes that
    /// no longer exist, and always refreshes the small top-level files
    /// (<c>model.json</c>, <c>manifest.json</c>, <c>index.json</c>).
    /// </summary>
    public static UpdateStats Update(GraphPayload graph, string outDir, string database, JsonSerializerOptions jsonOptions)
    {
        var build = Build(graph, database, jsonOptions);

        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
            WriteAll(outDir, build);
            return new UpdateStats
            {
                Nodes = build.Stats.Nodes,
                Edges = build.Stats.Edges,
                Objects = build.Stats.Objects,
                SharedNodes = build.Stats.SharedNodes,
                OrphanEdges = build.Stats.OrphanEdges,
                NodesByLabel = build.Stats.NodesByLabel,
                EdgesByType = build.Stats.EdgesByType,
                UnknownLabels = build.Stats.UnknownLabels,
                UnknownEdgeTypes = build.Stats.UnknownEdgeTypes,
                ObjectsWritten = build.ObjectFiles.Count,
                ObjectsUnchanged = 0,
                ObjectsRemoved = 0,
                SharedWritten = build.SharedFiles.Count,
                SharedUnchanged = 0,
                SharedRemoved = 0,
            };
        }

        var oldManifest = LoadManifest(outDir);

        // ── objects/<slug>/object.json: write only if content_hash changed ──
        var newManifest = JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(build.ManifestJson)
            ?? new Dictionary<string, ManifestEntry>();

        var objectsWritten = 0;
        var objectsUnchanged = 0;
        foreach (var (objId, entry) in newManifest)
        {
            var unchanged = oldManifest.TryGetValue(objId, out var oldEntry) && oldEntry.ContentHash == entry.ContentHash;
            if (unchanged)
            {
                objectsUnchanged++;
                continue;
            }

            var fullPath = Path.Combine(outDir, entry.ObjectFile);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, build.ObjectFiles[entry.ObjectFile], Encoding.UTF8);

            // nav.json is derived from the same object, so it changes iff the
            // object did - rewrite it in lockstep when the content_hash moves.
            if (!string.IsNullOrEmpty(entry.NavFile) && build.ObjectFiles.TryGetValue(entry.NavFile, out var navJson))
                File.WriteAllText(Path.Combine(outDir, entry.NavFile), navJson, Encoding.UTF8);
            objectsWritten++;
        }

        var objectsRemoved = 0;
        foreach (var (objId, oldEntry) in oldManifest)
        {
            if (newManifest.ContainsKey(objId))
                continue;

            var dir = Path.Combine(outDir, Path.GetDirectoryName(oldEntry.ObjectFile) ?? "");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            objectsRemoved++;
        }

        // ── shared/<category>/<slug>.json: write only if content changed ────
        var sharedWritten = 0;
        var sharedUnchanged = 0;
        foreach (var (relPath, json) in build.SharedFiles)
        {
            var fullPath = Path.Combine(outDir, relPath);
            if (File.Exists(fullPath) && File.ReadAllText(fullPath, Encoding.UTF8) == json)
            {
                sharedUnchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json, Encoding.UTF8);
            sharedWritten++;
        }

        // GC: any shared/**/*.json on disk that's no longer part of the store.
        var sharedRemoved = 0;
        var sharedDir = Path.Combine(outDir, "shared");
        if (Directory.Exists(sharedDir))
        {
            foreach (var file in Directory.EnumerateFiles(sharedDir, "*.json", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(outDir, file).Replace('\\', '/');
                if (build.SharedFiles.ContainsKey(rel))
                    continue;
                File.Delete(file);
                sharedRemoved++;
            }

            RemoveEmptyDirectories(sharedDir);
        }

        // ── always refresh the small top-level files ────────────────────────
        File.WriteAllText(Path.Combine(outDir, "model.json"), build.ModelJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), build.ManifestJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "index.json"), build.IndexJson, Encoding.UTF8);

        return new UpdateStats
        {
            Nodes = build.Stats.Nodes,
            Edges = build.Stats.Edges,
            Objects = build.Stats.Objects,
            SharedNodes = build.Stats.SharedNodes,
            OrphanEdges = build.Stats.OrphanEdges,
            NodesByLabel = build.Stats.NodesByLabel,
            EdgesByType = build.Stats.EdgesByType,
            UnknownLabels = build.Stats.UnknownLabels,
            UnknownEdgeTypes = build.Stats.UnknownEdgeTypes,
            ObjectsWritten = objectsWritten,
            ObjectsUnchanged = objectsUnchanged,
            ObjectsRemoved = objectsRemoved,
            SharedWritten = sharedWritten,
            SharedUnchanged = sharedUnchanged,
            SharedRemoved = sharedRemoved,
        };
    }

    private static Dictionary<string, ManifestEntry> LoadManifest(string outDir)
    {
        var path = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(path))
            return new Dictionary<string, ManifestEntry>();

        return JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(File.ReadAllText(path, Encoding.UTF8))
            ?? new Dictionary<string, ManifestEntry>();
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    private static void WriteAll(string outDir, BuildResult build)
    {
        foreach (var (relPath, json) in build.ObjectFiles)
        {
            var fullPath = Path.Combine(outDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json, Encoding.UTF8);
        }

        foreach (var (relPath, json) in build.SharedFiles)
        {
            var fullPath = Path.Combine(outDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json, Encoding.UTF8);
        }

        File.WriteAllText(Path.Combine(outDir, "model.json"), build.ModelJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), build.ManifestJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "index.json"), build.IndexJson, Encoding.UTF8);
    }

    /// <summary>
    /// Computes every document the node store consists of, without touching disk.
    /// Shared by <see cref="Write"/> (full regeneration) and <see cref="Update"/>
    /// (writes only what changed).
    /// </summary>
    private static BuildResult Build(GraphPayload graph, string database, JsonSerializerOptions jsonOptions)
    {
        var objectFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var sharedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        string modelJson;
        string manifestJson;
        string indexJson;
        Stats stats;

        var nodeById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
            nodeById[n.Id] = n; // last-writer-wins on duplicate ids, defensive

        var objectIds = new HashSet<string>(
            graph.Nodes.Where(n => n.Labels.Contains("SqlObject")).Select(n => n.Id),
            StringComparer.Ordinal);

        // The owning SqlObject of a node id: itself if it IS a SqlObject, the
        // "<ObjectId>" prefix before '#' for its Parameters/Variables/Steps, or
        // null for shared nodes (Table/Column/Action/Rule have no '#' and are
        // not SqlObjects).
        string? OwnerOf(string id)
        {
            if (objectIds.Contains(id))
                return id;
            var hashIdx = id.IndexOf('#');
            if (hashIdx > 0)
            {
                var prefix = id[..hashIdx];
                if (objectIds.Contains(prefix))
                    return prefix;
            }
            return null;
        }

        var pathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        string PathOf(string id)
        {
            if (pathCache.TryGetValue(id, out var cached))
                return cached;

            string result;
            if (objectIds.Contains(id))
            {
                result = $"objects/{Slug(id)}/object.json";
            }
            else
            {
                var owner = OwnerOf(id);
                result = owner != null
                    ? $"objects/{Slug(owner)}/object.json"
                    : $"shared/{SharedCategory(nodeById[id])}/{Slug(id)}_{ShortHash(id)}.json";
            }

            pathCache[id] = result;
            return result;
        }

        // Where a *navigation* hop should land: another object's tiny nav.json
        // (only inter-object edges) instead of its full object.json, so a call/
        // impact chain stays in ~KB-scale files per hop. Shared targets (a Table
        // a WRITES_TO lands on) have no nav.json, so they keep their normal path.
        string NavPathOf(string id) =>
            objectIds.Contains(id) ? $"objects/{Slug(id)}/nav.json" : PathOf(id);

        var nodesByLabel = new Dictionary<string, int>();
        var unknownLabels = new HashSet<string>();
        void TallyLabel(GraphNode n)
        {
            var primary = n.Labels.Count > 0 ? n.Labels[0] : "Node";
            nodesByLabel[primary] = nodesByLabel.GetValueOrDefault(primary) + 1;
            if (!KnownNodeLabels.Contains(primary))
                foreach (var lbl in n.Labels)
                    unknownLabels.Add(lbl);
        }

        // ── Classify every relationship by owner ────────────────────────────
        // edgesByObject:   object id -> edges it "owns" (its own subgraph, capa 2)
        // sharedRefsByNode: shared node id -> owning object id -> edges that land
        //                   on it from that object (provenance, for incremental GC)
        // sharedIntrinsicOut: shared node id -> edges that start at it (e.g.
        //                   HAS_COLUMN, FK_TO, REFERENCES, NESTED_IN, or a Rule's
        //                   GOVERNS into a Step) - the shared node's own edges_out
        var edgesByObject = new Dictionary<string, List<GraphRel>>(StringComparer.Ordinal);
        var sharedRefsByNode = new Dictionary<string, Dictionary<string, List<GraphRel>>>(StringComparer.Ordinal);
        var sharedIntrinsicOut = new Dictionary<string, List<GraphRel>>(StringComparer.Ordinal);
        var edgesByType = new Dictionary<string, int>();
        var unknownEdgeTypes = new HashSet<string>();
        var orphanEdges = 0;

        foreach (var rel in graph.Relationships)
        {
            edgesByType[rel.Type] = edgesByType.GetValueOrDefault(rel.Type) + 1;
            if (!KnownEdgeTypes.Contains(rel.Type))
                unknownEdgeTypes.Add(rel.Type);

            if (!nodeById.ContainsKey(rel.StartNodeId) || !nodeById.ContainsKey(rel.EndNodeId))
            {
                orphanEdges++;
                continue;
            }

            if (StructuralEdgeTypes.Contains(rel.Type))
                continue; // already implied by `owned.*` in objects/<obj>/object.json

            var startOwner = OwnerOf(rel.StartNodeId);
            var endOwner = OwnerOf(rel.EndNodeId);
            var scope = startOwner ?? endOwner;

            if (scope != null)
            {
                (edgesByObject.TryGetValue(scope, out var owned)
                    ? owned
                    : edgesByObject[scope] = new()).Add(rel);

                if (endOwner == null && !objectIds.Contains(rel.EndNodeId))
                {
                    var refs = sharedRefsByNode.TryGetValue(rel.EndNodeId, out var rd) ? rd : sharedRefsByNode[rel.EndNodeId] = new();
                    (refs.TryGetValue(scope, out var rl) ? rl : refs[scope] = new()).Add(rel);
                }
            }

            if (startOwner == null && !objectIds.Contains(rel.StartNodeId))
            {
                (sharedIntrinsicOut.TryGetValue(rel.StartNodeId, out var outList)
                    ? outList
                    : sharedIntrinsicOut[rel.StartNodeId] = new()).Add(rel);
            }
        }

        // ── Capa 2: objects/<slug>/object.json + manifest entries ───────────
        var manifest = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var objId in objectIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var objNode = nodeById[objId];
            TallyLabel(objNode);

            var ownedNodes = graph.Nodes.Where(n => n.Id != objId && OwnerOf(n.Id) == objId).ToList();
            foreach (var on in ownedNodes)
                TallyLabel(on);

            var edgesOut = new List<Dictionary<string, object>>();
            var sharedTouched = new SortedSet<string>(StringComparer.Ordinal);
            if (edgesByObject.TryGetValue(objId, out var rels))
            {
                foreach (var rel in rels
                    .OrderBy(r => r.StartNodeId, StringComparer.Ordinal)
                    .ThenBy(r => r.Type, StringComparer.Ordinal)
                    .ThenBy(r => r.EndNodeId, StringComparer.Ordinal))
                {
                    edgesOut.Add(EdgeEntry(rel, "from", "to", rel.StartNodeId, rel.EndNodeId, nodeById, PathOf));

                    if (OwnerOf(rel.EndNodeId) == null && !objectIds.Contains(rel.EndNodeId))
                        sharedTouched.Add(rel.EndNodeId);
                }
            }

            var doc = new Dictionary<string, object>
            {
                ["id"] = objId,
                ["labels"] = objNode.Labels,
                ["properties"] = objNode.Properties,
                ["owned"] = new Dictionary<string, object>
                {
                    ["parameters"] = ownedNodes.Where(n => n.Labels.Contains("Parameter")).Select(ToBag).ToList(),
                    ["variables"] = ownedNodes.Where(n => n.Labels.Contains("Variable")).Select(ToBag).ToList(),
                    ["steps"] = ownedNodes.Where(n => n.Labels.Contains("Step")).Select(ToBag).ToList(),
                },
                ["edges_out"] = edgesOut,
            };

            var relPath = $"objects/{Slug(objId)}/object.json";
            var json = JsonSerializer.Serialize(doc, jsonOptions);
            objectFiles[relPath] = json;

            // nav.json: the same edges_out, filtered to inter-object navigation
            // edges only (CALLS/WRITES_TO/READS_FROM/AFFECTS/FK_TO), each with its
            // `path` re-pointed at the neighbor's nav.json so a multi-object
            // traversal never has to open a full object.json per hop.
            var navEdges = edgesOut
                .Where(e => e.TryGetValue("type", out var t) && t is string ts && NavEdgeTypes.Contains(ts))
                .Select(e =>
                {
                    var copy = new Dictionary<string, object>(e);
                    if (e.TryGetValue("to", out var toObj) && toObj is string toId)
                        copy["path"] = NavPathOf(toId);
                    return copy;
                })
                .ToList();

            var navRelPath = $"objects/{Slug(objId)}/nav.json";
            objectFiles[navRelPath] = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = objId,
                ["name"] = DisplayName(objNode),
                ["edges_out"] = navEdges,
            }, jsonOptions);

            manifest[objId] = new Dictionary<string, object>
            {
                ["content_hash"] = StableHash(json),
                ["object_file"] = relPath,
                ["nav_file"] = navRelPath,
                ["shared_touched"] = sharedTouched.ToList(),
            };
        }

        // ── Capa 3: shared/<category>/<slug>.json ───────────────────────────
        var sharedNodeIds = graph.Nodes
            .Where(n => !objectIds.Contains(n.Id) && OwnerOf(n.Id) == null)
            .Select(n => n.Id)
            .ToList();

        foreach (var id in sharedNodeIds)
        {
            var node = nodeById[id];
            TallyLabel(node);

            var refs = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var edgesIn = new List<Dictionary<string, object>>();
            if (sharedRefsByNode.TryGetValue(id, out var byOwner))
            {
                foreach (var (ownerId, ownerRels) in byOwner.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var entries = ownerRels
                        .OrderBy(r => r.Type, StringComparer.Ordinal)
                        .ThenBy(r => r.StartNodeId, StringComparer.Ordinal)
                        .Select(rel => EdgeEntry(rel, "from", "to", rel.StartNodeId, rel.EndNodeId, nodeById, PathOf, dropTo: true))
                        .ToList();
                    refs[ownerId] = entries;
                    edgesIn.AddRange(entries);
                }
            }

            var edgesOut = sharedIntrinsicOut.TryGetValue(id, out var outRels)
                ? outRels
                    .OrderBy(r => r.Type, StringComparer.Ordinal)
                    .ThenBy(r => r.EndNodeId, StringComparer.Ordinal)
                    .Select(rel => EdgeEntry(rel, "from", "to", rel.StartNodeId, rel.EndNodeId, nodeById, PathOf, dropFrom: true))
                    .ToList()
                : new List<Dictionary<string, object>>();

            var doc = new Dictionary<string, object>
            {
                ["id"] = id,
                ["labels"] = node.Labels,
                ["properties"] = node.Properties,
                // refs: incoming edges partitioned by contributing object - the
                // unit a future incremental pass replaces wholesale.
                ["refs"] = refs,
                // edges_in: flattened union of refs, precomputed for direct reads.
                ["edges_in"] = edgesIn,
                ["edges_out"] = edgesOut,
            };

            var relPath = PathOf(id);
            sharedFiles[relPath] = JsonSerializer.Serialize(doc, jsonOptions);
        }

        // ── Capa 1: model.json (the "initial nodes") ────────────────────────
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        void Bump(string id) => degree[id] = degree.GetValueOrDefault(id) + 1;

        var modelEdges = new List<Dictionary<string, object>>();
        var macroSeen = new HashSet<(string Type, string From, string To)>();
        void AddMacroEdge(string type, string from, string to)
        {
            if (!macroSeen.Add((type, from, to)))
                return;
            modelEdges.Add(new Dictionary<string, object> { ["type"] = type, ["from"] = from, ["to"] = to });
            Bump(from);
            Bump(to);
        }

        foreach (var rel in graph.Relationships)
        {
            if (!nodeById.ContainsKey(rel.StartNodeId) || !nodeById.ContainsKey(rel.EndNodeId))
                continue;

            if (rel.Type is "CALLS" or "AFFECTS" or "FK_TO")
            {
                AddMacroEdge(rel.Type, rel.StartNodeId, rel.EndNodeId);
            }
            else if (rel.Type is "WRITES_TO" or "READS_FROM")
            {
                // Roll up from the owning SqlObject (Step -> Table becomes
                // SqlObject -> Table), so model.json stays object/table scale.
                var owner = OwnerOf(rel.StartNodeId);
                if (owner != null)
                    AddMacroEdge(rel.Type, owner, rel.EndNodeId);
            }
        }

        var modelNodes = graph.Nodes
            .Where(n => n.Labels.Contains("SqlObject") || n.Labels.Contains("Table"))
            .Select(n =>
            {
                var entry = new Dictionary<string, object>
                {
                    ["id"] = n.Id,
                    ["label"] = n.Labels.Count > 0 ? n.Labels[0] : "Node",
                    ["name"] = DisplayName(n),
                    ["path"] = PathOf(n.Id),
                    ["degree"] = degree.GetValueOrDefault(n.Id),
                };
                // For a SqlObject, surface the cheap entry point: follow `nav` (not
                // `path`) when chaining calls/writes/impact across objects; only
                // open `path` (object.json) when you need step/param/variable detail.
                //
                // Also roll up the per-object/per-table stats that a corpus-wide
                // report (rank by complexity, find SQL-dynamic-heavy procs, find
                // tables with no outgoing FK) needs - without this, answering those
                // questions means opening every object.json/shared/tables/*.json,
                // which costs as much as the monolithic graph_full.json and defeats
                // the NodeStore's per-file partitioning.
                if (objectIds.Contains(n.Id))
                {
                    entry["nav"] = NavPathOf(n.Id);
                    if (n.Properties.TryGetValue("cyclomatic_complexity", out var cc))
                        entry["cyclomatic_complexity"] = cc;

                    var steps = graph.Nodes.Where(s => s.Id != n.Id && OwnerOf(s.Id) == n.Id && s.Labels.Contains("Step")).ToList();
                    entry["total_steps"] = steps.Count;
                    entry["dynamic_sql_steps"] = steps.Count(s => s.Properties.TryGetValue("is_dynamic_sql", out var dyn) && dyn is true);
                }
                else if (n.Labels.Contains("Table"))
                {
                    entry["fk_out_count"] = sharedIntrinsicOut.TryGetValue(n.Id, out var outEdges)
                        ? outEdges.Count(e => e.Type == "FK_TO")
                        : 0;
                }
                return entry;
            })
            .ToList();

        modelJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["nodes"] = modelNodes,
            ["edges"] = modelEdges,
        }, jsonOptions);

        // ── manifest.json + index.json ──────────────────────────────────────
        manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);

        stats = new Stats
        {
            Nodes = graph.Nodes.Count,
            Edges = graph.Relationships.Count,
            Objects = objectIds.Count,
            SharedNodes = sharedNodeIds.Count,
            OrphanEdges = orphanEdges,
            NodesByLabel = nodesByLabel,
            EdgesByType = edgesByType,
            UnknownLabels = unknownLabels.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            UnknownEdgeTypes = unknownEdgeTypes.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };

        var index = new Dictionary<string, object>
        {
            ["meta"] = new Dictionary<string, object>
            {
                ["database"] = database,
                ["generated_at"] = DateTime.Now.ToString("o"),
                ["tool"] = "sql-analyzer/tsql-parser",
                ["format"] = "nodestore-v1",
            },
            // Machine-readable navigation contract, so an agent knows how to walk
            // the store without reverse-engineering it from a sample file.
            ["howto"] = new Dictionary<string, object>
            {
                ["start"] = "Load model.json: the initial nodes (every SqlObject and Table) with object/table-level edges.",
                ["pick"] = "From model.json, pick a node by id and follow its `path`.",
                ["open_object"] = "objects/<slug>/object.json holds a SqlObject's own properties, owned Parameters/Variables/Steps, and edges_out (each with `path` to the neighbor).",
                ["call_chain"] = "To follow a chain across objects (CALLS/WRITES_TO/READS_FROM/AFFECTS/FK_TO), read objects/<slug>/nav.json - NOT object.json. nav.json is a tiny file with only those inter-object edges, and each edge's `path` already points at the next object's nav.json, so the whole traversal stays in KB-scale files. From model.json, a SqlObject node's `nav` field is this cheap entry point. Open object.json only when you need a Step/Parameter/Variable detail (condition_path, is_dynamic_sql, etc.), not just the next hop.",
                ["corpus_report"] = "For a question about the WHOLE database (rank objects by complexity, find SQL-dynamic-heavy procs, find tables with no outgoing FK, etc.), read ONLY model.json - never loop over every object.json/shared/tables/*.json, that costs as much as graph_full.json and defeats the point of this store. Each SqlObject node already carries cyclomatic_complexity/total_steps/dynamic_sql_steps, and each Table node carries fk_out_count, precomputed so a corpus-wide report stays a single ~model.json-sized read.",
                ["open_shared"] = "shared/<category>/<slug>.json holds a Table/Column/Action/Rule: `refs` partitions its incoming edges by the SqlObject that contributed them, `edges_in`/`edges_out` give the flattened view.",
                ["downstream"] = "Follow edges_out (WRITES_TO, CALLS, AFFECTS) for what an object affects.",
                ["upstream"] = "Follow edges_in / refs (WRITES_TO, READS_FROM, CALLS) for what affects a node.",
                ["completeness"] = "model.json is EXHAUSTIVE for CALLS/AFFECTS/FK_TO and for WRITES_TO/READS_FROM rolled up to object/table scale (deduplicated from every Step that touches that table, including ones built from dynamic SQL). Cross-check counts against index.json's stats.edges_by_type if you need certainty. You never need to open an object's object.json just to discover more object-level edges of these types — they are already all in model.json.",
                ["exec_resolution"] = "An EXEC step resolves one of two ways, and model.json already has both rolled up: (1) a named-procedure call -> a CALLS edge object->object; (2) EXEC of a dynamically-built @variable (is_dynamic_sql=true on the Step, target_name='(dynamic SQL)') -> its inferred targets appear as WRITES_TO/READS_FROM edges with action_type in their `props`, not as CALLS. To see a Step's own is_dynamic_sql/dynamic_sql/target_name detail you do need objects/<slug>/object.json, but the object-level write/read targets themselves are already in model.json.",
            },
            ["schema"] = new Dictionary<string, object>
            {
                ["node_labels"] = KnownNodeLabels,
                ["edge_types"] = KnownEdgeTypes,
            },
            ["stats"] = new Dictionary<string, object>
            {
                ["total_nodes"] = stats.Nodes,
                ["total_edges"] = stats.Edges,
                ["objects"] = stats.Objects,
                ["shared_nodes"] = stats.SharedNodes,
                ["orphan_edges"] = stats.OrphanEdges,
                ["nodes_by_label"] = stats.NodesByLabel,
                ["edges_by_type"] = stats.EdgesByType,
                ["unknown_labels"] = stats.UnknownLabels,
                ["unknown_edge_types"] = stats.UnknownEdgeTypes,
            },
            ["entry"] = new Dictionary<string, object>
            {
                ["model"] = "model.json",
                ["manifest"] = "manifest.json",
                ["objects_dir"] = "objects/",
                ["shared_dir"] = "shared/",
            },
        };
        indexJson = JsonSerializer.Serialize(index, jsonOptions);

        return new BuildResult
        {
            ObjectFiles = objectFiles,
            SharedFiles = sharedFiles,
            ModelJson = modelJson,
            ManifestJson = manifestJson,
            IndexJson = indexJson,
            Stats = stats,
        };
    }

    /// <summary>
    /// One edge as seen from one side. <paramref name="fromKey"/>/<paramref name="toKey"/>
    /// name the "from"/"to" fields; <paramref name="dropTo"/>/<paramref name="dropFrom"/>
    /// omit the redundant self-reference (e.g. a shared node's own `refs` entries
    /// don't need to repeat "to": &lt;this node's id&gt;).
    /// </summary>
    private static Dictionary<string, object> EdgeEntry(
        GraphRel rel, string fromKey, string toKey, string fromId, string toId,
        Dictionary<string, GraphNode> nodeById, Func<string, string> pathOf,
        bool dropTo = false, bool dropFrom = false)
    {
        var entry = new Dictionary<string, object> { ["type"] = rel.Type };
        if (!dropFrom)
        {
            entry[fromKey] = fromId;
            entry[$"{fromKey}_label"] = nodeById[fromId].Labels.Count > 0 ? nodeById[fromId].Labels[0] : "Node";
        }
        if (!dropTo)
        {
            entry[toKey] = toId;
            entry[$"{toKey}_label"] = nodeById[toId].Labels.Count > 0 ? nodeById[toId].Labels[0] : "Node";
        }
        // The neighbor's file: when dropping "to" we're inside that neighbor's
        // own shared file, so point at the *other* (from) side instead.
        entry["path"] = dropTo ? pathOf(fromId) : pathOf(toId);
        if (rel.Properties.Count > 0)
            entry["props"] = rel.Properties;
        return entry;
    }

    private static Dictionary<string, object> ToBag(GraphNode n) => new()
    {
        ["id"] = n.Id,
        ["labels"] = n.Labels,
        ["properties"] = n.Properties,
    };

    /// <summary>Human-readable caption: full_name / name / label / expression, falling back to the id.</summary>
    private static string DisplayName(GraphNode n)
    {
        foreach (var key in new[] { "full_name", "name", "label", "expression" })
            if (n.Properties.TryGetValue(key, out var val) && val is string s && s.Length > 0)
                return s;
        return n.Id;
    }

    private static string SharedCategory(GraphNode n) => (n.Labels.Count > 0 ? n.Labels[0] : "Node") switch
    {
        "Table" => "tables",
        "Column" => "columns",
        "Action" => "actions",
        "Rule" => "rules",
        _ => "other",
    };

    // Keep [A-Za-z0-9.-], collapse every other run into a single '_'. Drops the
    // ':' / '#' / '*' that ids use as separators (illegal/awkward on Windows)
    // while staying readable.
    private static string Slug(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastUnderscore = false;
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            {
                sb.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }
        var slug = sb.ToString().Trim('_');
        return slug.Length > 80 ? slug[..80] : slug;
    }

    private static string ShortHash(string id) => StableHash(id)[..8];

    // SHA-1 hex of a string. Unlike string.GetHashCode (randomized per process),
    // this is stable across runs/platforms, so the same input always maps to the
    // same file/hash - the store is reproducible and diff-friendly.
    private static string StableHash(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    private static void PrepareDir(string outDir)
    {
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);
    }
}
