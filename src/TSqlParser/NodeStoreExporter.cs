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
///     objects/&lt;slug&gt;/lineage_path.json  per output column: precomputed root
///                            base-table column(s)/immediate precursor(s)/depth for
///                            its DERIVES_FROM chain (Tarea I) - only for SqlObjects
///                            with output columns; denormalized, always rebuilt in
///                            full, see its build site in <see cref="Build"/>.
///     shared/&lt;category&gt;/&lt;slug&gt;.json  one shared node + edges_in (by owner) + edges_out
/// </summary>
public static class NodeStoreExporter
{
    // Closed vocabularies: single source of truth in Parser.Contracts.Vocab
    // (shared with NetParser). Kept as aliases here so existing callers and the
    // index.json contract are unchanged.
    public static readonly IReadOnlyList<string> KnownNodeLabels = Vocab.KnownNodeLabels;

    public static readonly IReadOnlyList<string> KnownEdgeTypes = Vocab.KnownEdgeTypes;

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
    // "Navigation-worthy" edge types: the only ones worth following to move between
    // objects/tables/columns. DERIVES_FROM (column-to-column lineage) joined this set
    // for the same reason CALLS did - see the Caso 4/6 precedent in
    // docs/nodestore-analysis.md: a multi-hop lineage chain (view -> CTE -> table) is
    // structurally the cross-object call-chain problem, not the single-object
    // condition_path problem. Hopping through full shared/columns/*.json (each with its
    // `refs` partitioned by every contributing object) reproduces the Caso 4 regression
    // (more bytes read per hop, no fewer agent turns); a thin per-node nav.json (below)
    // is the fix that measurably worked there.
    private static readonly HashSet<string> NavEdgeTypes = new(StringComparer.Ordinal)
    {
        "CALLS", "WRITES_TO", "READS_FROM", "AFFECTS", "FK_TO", "CONTAINS", "DERIVES_FROM",
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
        public required string AuditJson { get; init; }
        public required string ChangeMapJson { get; init; }
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
            Utf8Io.WriteAllText(fullPath, build.ObjectFiles[entry.ObjectFile]);

            // nav.json is derived from the same object, so it changes iff the
            // object did - rewrite it in lockstep when the content_hash moves.
            if (!string.IsNullOrEmpty(entry.NavFile) && build.ObjectFiles.TryGetValue(entry.NavFile, out var navJson))
                Utf8Io.WriteAllText(Path.Combine(outDir, entry.NavFile), navJson);
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

        // lineage_path.json (Tarea I): denormalized cache recomputed in full on
        // every Build, never gated by content_hash (see comment at its build site) -
        // an object's own object.json may be unchanged while an upstream object it
        // chains through changed its DERIVES_FROM, so content_hash equality doesn't
        // imply the lineage paths are still correct. Write/delete unconditionally
        // for every currently-existing object.
        foreach (var objId in newManifest.Keys)
        {
            var lpRelPath = $"objects/{Slug(objId)}/lineage_path.json";
            var lpFullPath = Path.Combine(outDir, lpRelPath);
            if (build.ObjectFiles.TryGetValue(lpRelPath, out var lpJson))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lpFullPath)!);
                Utf8Io.WriteAllText(lpFullPath, lpJson);
            }
            else if (File.Exists(lpFullPath))
            {
                File.Delete(lpFullPath);
            }
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
            Utf8Io.WriteAllText(fullPath, json);
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
        Utf8Io.WriteAllText(Path.Combine(outDir, "model.json"), build.ModelJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "manifest.json"), build.ManifestJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "index.json"), build.IndexJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "audit_report.json"), build.AuditJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "change_map.json"), build.ChangeMapJson);

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
            Utf8Io.WriteAllText(fullPath, json);
        }

        foreach (var (relPath, json) in build.SharedFiles)
        {
            var fullPath = Path.Combine(outDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            Utf8Io.WriteAllText(fullPath, json);
        }

        Utf8Io.WriteAllText(Path.Combine(outDir, "model.json"), build.ModelJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "manifest.json"), build.ManifestJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "index.json"), build.IndexJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "audit_report.json"), build.AuditJson);
        Utf8Io.WriteAllText(Path.Combine(outDir, "change_map.json"), build.ChangeMapJson);
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

        // Where a *navigation* hop should land: a tiny nav.json (only NavEdgeTypes
        // edges) instead of the full object.json/shared file, so a call/impact/lineage
        // chain stays in ~KB-scale files per hop. Every object AND every shared node
        // (Table/Column/...) gets one - a DERIVES_FROM chain across several view/table
        // columns hops nav.json -> nav.json, never opening a full shared/columns/*.json
        // (which carries `refs` partitioned by every contributing object and can be large
        // for a popular column).
        string SharedNavPathOf(string id) => $"shared/{SharedCategory(nodeById[id])}/{Slug(id)}_{ShortHash(id)}.nav.json";
        string NavPathOf(string id) =>
            objectIds.Contains(id) ? $"objects/{Slug(id)}/nav.json" : SharedNavPathOf(id);

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
        // edges_in counterpart of sharedIntrinsicOut: when BOTH ends of an edge are
        // shared nodes owned by no object (scope == null below - e.g. a DERIVES_FROM
        // from one table-scheme Column straight to another, neither owned by a
        // SqlObject), sharedRefsByNode never sees it (it's only populated when
        // scope != null) so the target's edges_in/refs would silently miss it. The
        // forward direction (provenance: column -> what it derives FROM) already works
        // via sharedIntrinsicOut; this fixes the reverse (impact: column -> what
        // consumes it) for the same scope-less case. See docs/agent-collab.md and
        // docs/lineage-perfect-discussion.md SS1.2.
        var sharedIntrinsicIn = new Dictionary<string, List<GraphRel>>(StringComparer.Ordinal);
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

            // Complement of the block above: scope == null means neither end is
            // owned by an object, so the existing `if (scope != null)` branch never
            // ran for this edge - the end node's incoming side needs its own path.
            if (scope == null && endOwner == null && !objectIds.Contains(rel.EndNodeId))
            {
                (sharedIntrinsicIn.TryGetValue(rel.EndNodeId, out var inList)
                    ? inList
                    : sharedIntrinsicIn[rel.EndNodeId] = new()).Add(rel);
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
            // Scope-less incoming edges (both ends are unowned shared nodes, e.g. a
            // DERIVES_FROM from one table-scheme Column to another) have no
            // contributing object to partition `refs` by, so they go straight into
            // the flattened edges_in instead - see sharedIntrinsicIn above.
            if (sharedIntrinsicIn.TryGetValue(id, out var intrinsicInRels))
            {
                edgesIn.AddRange(intrinsicInRels
                    .OrderBy(r => r.Type, StringComparer.Ordinal)
                    .ThenBy(r => r.StartNodeId, StringComparer.Ordinal)
                    .Select(rel => EdgeEntry(rel, "from", "to", rel.StartNodeId, rel.EndNodeId, nodeById, PathOf, dropTo: true)));
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

            // nav.json: edges_in/edges_out filtered to NavEdgeTypes, each `path`
            // re-pointed at the neighbor's own nav.json - see NavPathOf above. Always
            // emitted (even if empty) so a consumer never has to special-case "does this
            // node have a nav file" - mirrors the unconditional object nav.json.
            Dictionary<string, object> ToNavEdge(Dictionary<string, object> e)
            {
                var copy = new Dictionary<string, object>(e);
                if (e.TryGetValue("to", out var toObj) && toObj is string toId)
                    copy["path"] = NavPathOf(toId);
                if (e.TryGetValue("from", out var fromObj) && fromObj is string fromId)
                    copy["path"] = NavPathOf(fromId);
                return copy;
            }
            var navIn = edgesIn.Where(e => e.TryGetValue("type", out var t) && t is string ts && NavEdgeTypes.Contains(ts)).Select(ToNavEdge).ToList();
            var navOut = edgesOut.Where(e => e.TryGetValue("type", out var t) && t is string ts && NavEdgeTypes.Contains(ts)).Select(ToNavEdge).ToList();
            sharedFiles[SharedNavPathOf(id)] = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = DisplayName(node),
                ["edges_in"] = navIn,
                ["edges_out"] = navOut,
            }, jsonOptions);
        }

        // ── Capa 4: objects/<slug>/lineage_path.json (Tarea I) ──────────────
        // For every SqlObject with output columns (HAS_COLUMN edges it owns - views,
        // TVFs, procedures with OUTPUT; a plain base table has no SqlObject and is
        // skipped), precompute per output column the ultimate root base-table
        // column(s) it DERIVES_FROM, its immediate precursor(s), and the longest
        // path depth - format per docs/lineage-path-spec.md. Walking this chain by
        // hand (even via nav.json) measurably costs 7-10 agent turns for a 3-hop
        // chain with zero advantage from nav.json over full files (unlike CALLS
        // chains, where nav.json does help) - see docs/nodestore-analysis.md Caso 7.
        // This collapses that to one file read. Memoized across ALL objects (a
        // column shared by several chains, e.g. an intermediate view column, is
        // traced once). Emitted unconditionally on every Build (both Write and
        // Update - see callers): it's a denormalized cache purely derived from
        // already-computed DERIVES_FROM edges, cheap to recompute in full, not
        // worth incremental content-hash diffing.
        var derivesFromOut = new Dictionary<string, List<GraphRel>>(StringComparer.Ordinal);
        foreach (var rel in graph.Relationships)
        {
            if (rel.Type != "DERIVES_FROM" || !nodeById.ContainsKey(rel.StartNodeId) || !nodeById.ContainsKey(rel.EndNodeId))
                continue;
            (derivesFromOut.TryGetValue(rel.StartNodeId, out var list) ? list : derivesFromOut[rel.StartNodeId] = new()).Add(rel);
        }

        string ColumnDisplayName(string columnId)
        {
            var props = nodeById[columnId].Properties;
            var table = props.TryGetValue("table", out var t) && t is string ts ? ts : "";
            var name = props.TryGetValue("name", out var nm) && nm is string ns ? ns : columnId;
            return table.Length > 0 ? $"{table}.{name}" : name;
        }

        var lineageCache = new Dictionary<string, (List<string> Roots, int Depth)>(StringComparer.Ordinal);
        (List<string> Roots, int Depth) TraceLineage(string columnId, HashSet<string> recursionStack)
        {
            if (lineageCache.TryGetValue(columnId, out var cached))
                return cached;
            // Cycle guard: currentNode is already being traced higher up this same
            // call stack (e.g. a recursive CTE). Stop this branch with an empty
            // result WITHOUT caching it - a different top-level call may reach this
            // node outside any cycle and must be free to resolve it properly.
            if (!recursionStack.Add(columnId))
                return (new List<string>(), 0);

            (List<string> Roots, int Depth) result;
            if (!derivesFromOut.TryGetValue(columnId, out var precursors) || precursors.Count == 0)
            {
                // Base case: no outgoing DERIVES_FROM - this column is itself a root.
                result = (new List<string> { columnId }, 0);
            }
            else
            {
                var rootIds = new List<string>();
                var rootSeen = new HashSet<string>(StringComparer.Ordinal);
                var maxDepth = -1;
                foreach (var rel in precursors)
                {
                    var (subRoots, subDepth) = TraceLineage(rel.EndNodeId, recursionStack);
                    foreach (var r in subRoots)
                        if (rootSeen.Add(r))
                            rootIds.Add(r);
                    maxDepth = Math.Max(maxDepth, subDepth);
                }
                result = (rootIds, maxDepth + 1);
            }

            recursionStack.Remove(columnId);
            lineageCache[columnId] = result;
            return result;
        }

        foreach (var objId in objectIds)
        {
            if (!edgesByObject.TryGetValue(objId, out var ownedRels))
                continue;

            var outputColumns = ownedRels
                .Where(r => r.Type == "HAS_COLUMN" && r.StartNodeId == objId && nodeById.ContainsKey(r.EndNodeId))
                .Select(r => r.EndNodeId)
                .Distinct()
                .ToList();
            if (outputColumns.Count == 0)
                continue; // not a view/TVF/proc-with-OUTPUT - no lineage_path.json for it

            var perColumn = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var colId in outputColumns)
            {
                var colName = nodeById[colId].Properties.TryGetValue("name", out var nm) && nm is string ns ? ns : colId;
                var (roots, depth) = TraceLineage(colId, new HashSet<string>(StringComparer.Ordinal));
                var immediate = derivesFromOut.TryGetValue(colId, out var direct)
                    ? direct.Select(r => ColumnDisplayName(r.EndNodeId)).Distinct().ToList()
                    : new List<string>();

                perColumn[colName] = new Dictionary<string, object?>
                {
                    ["roots"] = roots.Select(ColumnDisplayName).Distinct().ToList(),
                    ["immediate"] = immediate,
                    ["depth"] = depth,
                    ["transformation_summary"] = null, // stretch goal, fase 3.2b - ver docs/lineage-perfect-discussion.md SS1.1
                };
            }

            objectFiles[$"objects/{Slug(objId)}/lineage_path.json"] = JsonSerializer.Serialize(perColumn, jsonOptions);
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

        // Base table name -> degree, used below to recognize system-versioned temporal
        // history tables (<Table>_Archive, auto-populated by the engine on UPDATE/DELETE
        // of <Table>, never referenced by name in application T-SQL) so they're labeled
        // "historial temporal, esperado" instead of looking like an orphaned/unused table.
        var tableDegreeByName = graph.Nodes
            .Where(n => n.Labels.Contains("Table"))
            .ToDictionary(DisplayName, n => degree.GetValueOrDefault(n.Id), StringComparer.OrdinalIgnoreCase);

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
                    // Of those, how many never resolved to a literal (dynamic_sql == ""):
                    // these run real SQL at execution time (often a real table read/write)
                    // that READS_FROM/WRITES_TO can't see - the parser fails closed (emits
                    // nothing) rather than guessing, so without this count an agent reading
                    // edges_out/model.json would wrongly read "no more reads/writes" as
                    // "this object provably doesn't touch anything else".
                    entry["unresolved_dynamic_sql_steps"] = steps.Count(s =>
                        s.Properties.TryGetValue("is_dynamic_sql", out var dyn) && dyn is true
                        && (!s.Properties.TryGetValue("dynamic_sql", out var dsql) || dsql is not string { Length: > 0 }));
                }
                else if (n.Labels.Contains("Table"))
                {
                    entry["fk_out_count"] = sharedIntrinsicOut.TryGetValue(n.Id, out var outEdges)
                        ? outEdges.Count(e => e.Type == "FK_TO")
                        : 0;

                    var name = DisplayName(n);
                    if (degree.GetValueOrDefault(n.Id) == 0
                        && name.EndsWith("_Archive", StringComparison.OrdinalIgnoreCase)
                        && tableDegreeByName.GetValueOrDefault(name[..^"_Archive".Length], 0) > 0)
                    {
                        entry["classification"] = "historial temporal, esperado";
                    }
                }
                return entry;
            })
            .ToList();

        // ── Workflows (Capa 1 extension: appended to model.json) ─────────────
        // Call chains from entry-point procs/functions (in-degree 0 in CALLS
        // subgraph) to leaves, for change-strategy planning bottom-up.
        // Triggers excluded in v1 (event-driven; not invocable). Conditions on
        // hops are v2 (CALLS edges are SqlObject→SqlObject — step context lost).
        var callsOutAdj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var callsHasIncoming = new HashSet<string>(StringComparer.Ordinal);
        var triggerIds = new HashSet<string>(
            graph.Nodes.Where(n => n.Labels.Contains("SqlObject") &&
                n.Properties.TryGetValue("object_type", out var ot) && ot is string ots && ots == "TRIGGER")
            .Select(n => n.Id), StringComparer.Ordinal);
        var procFuncIds = new HashSet<string>(objectIds.Except(triggerIds), StringComparer.Ordinal);

        foreach (var edge in modelEdges)
        {
            if ((string)edge["type"] != "CALLS") continue;
            var from = (string)edge["from"];
            var to   = (string)edge["to"];
            if (!procFuncIds.Contains(from) || !procFuncIds.Contains(to)) continue;
            if (!callsOutAdj.TryGetValue(from, out var tgts))
                callsOutAdj[from] = tgts = [];
            tgts.Add(to);
            // A self-call (direct recursion) doesn't count as "has an external
            // caller" - an object that only calls itself is still its own entry
            // point and must appear in entryPoints below, not be swallowed as if
            // something else called it.
            if (from != to)
                callsHasIncoming.Add(to);
        }

        var entryPoints = procFuncIds
            .Where(id => !callsHasIncoming.Contains(id) && callsOutAdj.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var modelWorkflows = new List<object>();
        foreach (var entry in entryPoints)
        {
            if (!nodeById.TryGetValue(entry, out var entryNode)) continue;
            var paths = new List<object>();
            var hops  = new List<(string From, string To)>();
            var pathVis = new HashSet<string>(StringComparer.Ordinal);
            BuildWorkflowPaths(entry, callsOutAdj, nodeById, pathVis, hops, paths,
                               maxDepth: 10, maxPaths: 30, DisplayName);
            if (paths.Count == 0) continue;
            modelWorkflows.Add(new Dictionary<string, object>
            {
                ["entry"]      = entry,
                ["entry_name"] = DisplayName(entryNode),
                ["entry_type"] = entryNode.Properties.TryGetValue("object_type", out var et)
                                 && et is string ets ? ets : "",
                ["paths"]      = paths,
            });
        }

        modelJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["nodes"]     = modelNodes,
            ["edges"]     = modelEdges,
            ["workflows"] = modelWorkflows,
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
                ["completeness"] = "model.json is EXHAUSTIVE for CALLS/AFFECTS/FK_TO and for WRITES_TO/READS_FROM rolled up to object/table scale (deduplicated from every Step that touches that table, including ones built from dynamic SQL) - WITH ONE EXCEPTION: dynamic SQL that never resolved to a literal at analysis time (see unresolved_dynamic_sql_steps below) touches real tables at runtime that genuinely have no edge anywhere in this store - the parser fails closed (emits nothing) rather than guessing a wrong table. For every other case, cross-check counts against index.json's stats.edges_by_type if you need certainty; you never need to open an object's object.json just to discover more object-level edges of these types.",
                ["exec_resolution"] = "An EXEC step resolves one of three ways: (1) a named-procedure call -> a CALLS edge object->object, already in model.json; (2) EXEC of a dynamically-built @variable that fully resolved to a literal (is_dynamic_sql=true, target_name='(dynamic SQL)', but the Step's own dynamic_sql text is non-empty) -> its real targets appear as WRITES_TO/READS_FROM edges with action_type in their `props`, also already in model.json; (3) EXEC of a dynamically-built @variable that could NOT be resolved to a literal (is_dynamic_sql=true AND dynamic_sql=='') -> no edge exists anywhere for what it touches - this is the one real gap in this store's lineage, counted per object in unresolved_dynamic_sql_steps so you know when an object's WRITES_TO/READS_FROM set is provably incomplete rather than provably empty. To see a Step's own is_dynamic_sql/dynamic_sql/target_name detail you do need objects/<slug>/object.json.",
                ["unresolved_dynamic_sql_steps"] = "Each SqlObject node in model.json carries unresolved_dynamic_sql_steps: count of its EXEC steps where is_dynamic_sql=true and the dynamic SQL text never resolved to a literal (dynamic_sql==''). A value >0 means this object's WRITES_TO/READS_FROM edges are an undercount, not the full picture - treat it like a missing-data flag, not a 0/total-steps style metric.",
                ["column_lineage"] = "To find where an output column's data ultimately comes from (its root base-table column(s)), read objects/<slug>/lineage_path.json - NOT nav.json hopping. It's keyed by output column name; each entry precomputes `roots` (root base-table column(s), as table.column strings), `immediate` (direct precursor column(s), same format), `depth` (longest path to a root), and `transformation_summary` (reserved, currently always null). This is a single O(1) read - measured (docs/nodestore-analysis.md Caso 7) that walking DERIVES_FROM hop by hop costs 7-10 agent turns for just a 3-hop chain, with NO advantage from nav.json over full files (unlike CALLS chains, where nav.json does help - see call_chain above). lineage_path.json only exists for SqlObjects with output columns (views, TVFs, procedures with OUTPUT); a plain base table has none.",
                ["workflows_and_impact"] = "For 'what runs from here' / 'whom do I impact' / any transitive-reach question, read change_map.json at the store root - do NOT walk CALLS chains through nav.json for this (measured in docs/nodestore-analysis.md Caso 4: an agent walking the chain reads ~2x MORE lines than the monolithic graph; the precomputed answer is one ~KB-scale read). change_map.workflows lists every entry-point-to-leaves CALLS path with per-hop conditionality (the hop carries conditional/condition/condition_stack from the calling Step's condition_path); change_map.impact[<objectId>] carries via_calls (the FULL transitive callee closure, with depth and the condition first reaching each callee) and via_data (every table the object writes -> the objects that read it). nav.json remains the right tool only when you need the actual hop-by-hop route or edges change_map does not cover (FK_TO, AFFECTS).",
                ["business_rules"] = "Rules (IF/WHILE/CASE guards promoted to Rule nodes) live in shared/rules/<slug>.json, linked Rule-GOVERNS->Step; a shared rule's `refs` shows every SqlObject applying it. For the conditions wrapping one specific Step, read that object's object.json: each Step carries condition_path (the stack of conditions it executes under). For hop conditionality on a call chain you do NOT need either - change_map already rolls the calling Step's condition into each workflow hop and via_calls entry (see workflows_and_impact).",
                ["risk_audit"] = "For hotspots, blind spots, risk patterns (bad practices) and lineage-coverage percentages, read audit_report.json at the store root - it is a corpus-wide precomputed aggregate; never derive these by looping over objects.",
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
                ["change_map"] = "change_map.json",
                ["audit_report"] = "audit_report.json",
                ["objects_dir"] = "objects/",
                ["shared_dir"] = "shared/",
            },
        };
        indexJson = JsonSerializer.Serialize(index, jsonOptions);

        // ── Capa 5: audit_report.json ────────────────────────────────────────
        // Cross-graph aggregate (hotspots, blind_spots, risk_patterns, orphans,
        // lineage coverage). Always rebuilt in full — content-hashing individual
        // objects is wrong because a single upstream change shifts every entry.
        var auditJson = AuditExporter.Generate(graph, lineageCache, jsonOptions);

        // ── Capa 6: change_map.json ──────────────────────────────────────────
        // Precomputed workflows (entry-point CALLS paths with per-hop conditions)
        // + per-object impact closure (via_calls / via_data). Like audit_report,
        // always rebuilt in full: a single object change can reshape paths and
        // closures store-wide. Spec: docs/task-change-map.md (Tarea J P1-P7).
        var changeMapJson = ChangeMapExporter.Generate(graph, lineageCache, jsonOptions);

        return new BuildResult
        {
            ObjectFiles = objectFiles,
            SharedFiles = sharedFiles,
            ModelJson = modelJson,
            ManifestJson = manifestJson,
            IndexJson = indexJson,
            AuditJson = auditJson,
            ChangeMapJson = changeMapJson,
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
        "Database" => "databases",
        "Schema" => "schemas",
        "BusinessRule" => "businessrules",
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

    private static void BuildWorkflowPaths(
        string current,
        Dictionary<string, List<string>> callsOut,
        Dictionary<string, GraphNode> nodeById,
        HashSet<string> pathVisited,
        List<(string From, string To)> currentHops,
        List<object> paths,
        int maxDepth,
        int maxPaths,
        Func<GraphNode, string> displayName)
    {
        if (paths.Count >= maxPaths) return;

        bool isLeaf = !callsOut.ContainsKey(current) || currentHops.Count >= maxDepth;
        if (isLeaf)
        {
            if (currentHops.Count > 0)
                paths.Add(SerializeWorkflowPath(currentHops, nodeById, displayName, cycleTarget: null));
            return;
        }

        pathVisited.Add(current);
        foreach (var target in callsOut[current].OrderBy(t => t, StringComparer.Ordinal))
        {
            if (paths.Count >= maxPaths) break;
            currentHops.Add((current, target));
            if (pathVisited.Contains(target))
                paths.Add(SerializeWorkflowPath(currentHops, nodeById, displayName, cycleTarget: target));
            else
                BuildWorkflowPaths(target, callsOut, nodeById, pathVisited, currentHops, paths, maxDepth, maxPaths, displayName);
            currentHops.RemoveAt(currentHops.Count - 1);
        }
        pathVisited.Remove(current);
    }

    private static object SerializeWorkflowPath(
        List<(string From, string To)> hops,
        Dictionary<string, GraphNode> nodeById,
        Func<GraphNode, string> displayName,
        string? cycleTarget)
    {
        var serialized = hops.Select((h, i) =>
        {
            var entry = new Dictionary<string, object?>
            {
                ["from"]    = nodeById.TryGetValue(h.From, out var fn) ? displayName(fn) : h.From,
                ["from_id"] = h.From,
                ["to"]      = nodeById.TryGetValue(h.To,   out var tn) ? displayName(tn) : h.To,
                ["to_id"]   = h.To,
            };
            if (cycleTarget != null && i == hops.Count - 1)
                entry["cycle_back_to"] = cycleTarget;
            return (object)entry;
        }).ToList();
        return new Dictionary<string, object> { ["hops"] = serialized };
    }
}
