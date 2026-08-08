namespace Parser.Contracts;

/// <summary>
/// Closed vocabularies shared by every extractor and exporter. Emitted into the
/// nodestore's index.json as the store's contract; anything outside them is
/// flagged in stats rather than silently dropped, so a reader can rely on the
/// type set. App-side labels/edges are defined in docs/task-app-bridge.md.
/// </summary>
public static class Vocab
{
    public static readonly IReadOnlyList<string> KnownNodeLabels = new[]
    {
        // SQL side (GraphExporter)
        "SqlObject", "Process", "Workflow", "Parameter", "Variable", "Step", "Action", "Table", "Column", "Rule",
        "Database", "Schema", "BusinessRule",
        // App side (NetParser). EntryPoint and ExternalTarget are the symmetric pair:
        // where a flow starts (an HTTP route, a Main, a hosted service, a UI handler)
        // and where it leaves the process (see Boundary). Neither is tied to a
        // project type or a protocol — that lives in their "kind"/"protocol" props.
        "AppSolution", "AppProject", "AppPackage", "AppFile", "AppClass", "AppMethod", "AppNamespace",
        "EntryPoint", "ExternalTarget",
    };

    /// <summary>
    /// How a flow can start. Open-ended by design: one more project type is one more
    /// value. Scope is C# server-side code — desktop UI (WinForms/WPF/MAUI) and
    /// WebForms are deliberately absent, not pending.
    /// </summary>
    public static readonly IReadOnlyList<string> EntryPointKinds = new[]
    {
        "http_route",      // MVC/Web API action, minimal API
        "console_main",    // console app, batch job, scheduled task
        "hosted_service",  // BackgroundService/IHostedService/Windows service
        "job",             // Hangfire/Quartz/timer-triggered work
        "function",        // Azure Function / serverless trigger
        "message_handler", // broker subscription, MediatR/NServiceBus handler
        "library_api",     // public surface of a class library: called from outside the solution
    };

    public static readonly IReadOnlyList<string> KnownEdgeTypes = new[]
    {
        // SQL side (GraphExporter)
        "HAS_PARAMETER", "DECLARES", "ASSIGNED_FROM", "HAS_STEP", "ACTION", "BUILDS_SQL_FROM",
        "USES_VARIABLE", "TARGETS", "WRITES_TO", "READS_FROM", "READS_COLUMN", "WRITES_COLUMN",
        "FILTERS_ON", "DERIVES_FROM", "CONDITIONED_BY", "NESTED_IN", "GOVERNS", "CALLS", "AFFECTS", "HAS_COLUMN", "FK_TO", "REFERENCES",
        "BELONGS_TO", "WORKFLOW_WRITES_TO", "CONTAINS", "HAS_RULE", "CONSTRAINS",
        // Dynamic-trigger layer: a proc CREATES a trigger, the trigger fires ON a table.
        "CREATES", "ON",
        // App side (NetParser): project/package deps, method->SqlObject|Table bridge,
        // EF entity->table mapping
        "DEPENDS_ON", "EXECUTES_SQL", "MAPS_TO", "IMPLEMENTS", "EXPOSES", Boundary.ExternalEdge,
    };

    /// <summary>
    /// Edges that cross out of the process. A consumer asking "what infrastructure
    /// does this project touch?" selects these, not a hardcoded list per protocol.
    /// </summary>
    public static readonly IReadOnlyList<string> BoundaryEdgeTypes = new[]
    {
        "EXECUTES_SQL", Boundary.ExternalEdge,
    };
}
