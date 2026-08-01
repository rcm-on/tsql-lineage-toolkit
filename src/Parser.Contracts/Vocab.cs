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
        // App side (NetParser)
        "AppSolution", "AppProject", "AppPackage", "AppFile", "AppClass", "AppMethod", "AppEndpoint", "ExternalService",
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
        "DEPENDS_ON", "EXECUTES_SQL", "MAPS_TO", "IMPLEMENTS", "EXPOSES",
    };
}
