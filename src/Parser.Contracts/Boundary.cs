namespace Parser.Contracts;

/// <summary>
/// The contract every "the code leaves the process here" edge shares, whatever the
/// protocol: SQL today, HTTP today, queues/cache/files/gRPC when a resolver for them
/// is written. The pipeline is always the same three stages — recognise the sink,
/// resolve the argument that names the target, resolve that name against a catalog —
/// and only the catalog changes (model.json for SQL, a route table for HTTP, a topic
/// list for a broker). Keeping the property set uniform is what makes a new protocol
/// a resolver instead of a redesign of the exporters and the queries.
///
/// Two shapes of boundary edge, on purpose:
///   - resolved into a known catalog node: the edge points straight at it and keeps
///     its domain type (EXECUTES_SQL -> SqlObject|Table). Impact traversal works.
///   - not resolved (external or unknown): the edge is CALLS_EXTERNAL and points at
///     an ExternalTarget node, so an unresolved call is still visible infrastructure
///     rather than a silent gap.
/// </summary>
public static class Boundary
{
    /// <summary>What the call crosses into. Closed vocabulary.</summary>
    public static readonly IReadOnlyList<string> Protocols = new[]
    {
        "sql", "http", "queue", "cache", "file", "grpc",
    };

    /// <summary>How much the target is trusted, mirroring the SQL bridge's scale.</summary>
    public static readonly IReadOnlyList<string> Confidences = new[]
    {
        "EXTRACTED",   // the target was a literal at the call site
        "RESOLVED",    // reconstructed by data flow (call-site narrowing, config)
        "AMBIGUOUS",   // several catalog candidates, off the impact path by default
        "UNRESOLVED",  // the sink is real, the target is not knowable statically
    };

    /// <summary>
    /// The mechanism that produced the target — the honest limit of the analysis,
    /// reported per edge so it can be measured and improved without a schema change.
    /// </summary>
    public static readonly IReadOnlyList<string> Resolutions = new[]
    {
        "literal",        // string literal in the call
        "local_flow",     // const/local/interpolation resolved inside the method
        "interproc_1",    // narrowed from literal arguments one call level up
        "catalog_match",  // a template matched against catalog entries
        "unresolved",     // nothing but the sink itself
    };

    public const string TargetLabel = "ExternalTarget";

    public const string ExternalEdge = "CALLS_EXTERNAL";

    /// <summary>Stable id for a boundary target: app::ext:&lt;protocol&gt;:&lt;key&gt;.</summary>
    public static string TargetId(string protocol, string key) => $"app::ext:{protocol}:{key}";

    /// <summary>Key used when the sink is recognised but its target is not knowable.</summary>
    public const string UnknownKey = "unknown";

    /// <summary>The uniform property bag; callers add their protocol-specific extras.</summary>
    public static Dictionary<string, object> Props(
        string protocol, string confidence, string resolution, string targetRaw, int line, string file)
        => new()
        {
            ["protocol"] = protocol,
            ["confidence"] = confidence,
            ["resolution"] = resolution,
            ["target_raw"] = targetRaw,
            ["line"] = line,
            ["file"] = file,
        };
}
