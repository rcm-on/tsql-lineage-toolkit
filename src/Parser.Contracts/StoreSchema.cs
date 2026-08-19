namespace Parser.Contracts;

/// <summary>
/// Contrato del store SQLite: lo que escribe el exportador y leen el MCP y
/// scripts/lineage-queries.sql. Vive en Contracts, no junto al exportador, porque ata a
/// productor y consumidor: un renombrado debe romper la compilación de los dos.
/// </summary>
public static class StoreSchema
{
    public const string FormatVersion = "graph-sqlite-v1";

    public static class Tables
    {
        public const string Nodes = "nodes";
        public const string Edges = "edges";
        public const string Meta = "meta";
    }

    /// <summary>Claves de la tabla meta.</summary>
    public static class MetaKeys
    {
        public const string Database = "database";
        public const string Project = "project";
        public const string GeneratedAt = "generated_at";
        public const string Tool = "tool";
        public const string Format = "format";
        public const string NodeCount = "node_count";
        public const string EdgeCount = "edge_count";
    }

    /// <summary>Id de un Step: "&lt;objId&gt;#stepN".</summary>
    public const char StepIdSeparator = '#';

    /// <summary>Enrolla un id de Step a su SqlObject dueño; cualquier otro id pasa igual.</summary>
    public static string RollUpStep(string id)
    {
        var i = id.IndexOf(StepIdSeparator);
        return i > 0 ? id[..i] : id;
    }

    /// <summary>Valores de la propiedad `resolution` en aristas de columna/tabla.</summary>
    public static class Resolution
    {
        public const string Direct = "direct";
        public const string StarExpanded = "star_expanded";
        public const string ViaView = "via_view";
    }

    /// <summary>
    /// Subconjunto de <see cref="Vocab.KnownEdgeTypes"/> que recorre el análisis de
    /// impacto. Un gate verifica la inclusión: si Vocab renombra un tipo, salta.
    /// </summary>
    public static readonly IReadOnlyList<string> ImpactEdgeTypes =
    [
        "CALLS", "READS_FROM", "WRITES_TO", "DERIVES_FROM", "READS_COLUMN", "WRITES_COLUMN",
    ];

    /// <summary>Aristas de referencia a columna, las que clasifica `resolution`.</summary>
    public static readonly IReadOnlyList<string> ColumnRefEdgeTypes =
    [
        "READS_COLUMN", "WRITES_COLUMN", "FILTERS_ON",
    ];

    /// <summary>
    /// Labels que un agente puede nombrar. El resto (Step, Action, Variable, Rule...) es
    /// fontanería del grafo, no objetos direccionables.
    /// </summary>
    public static readonly IReadOnlyList<string> AddressableLabels =
    [
        "SqlObject", "Table", "Column",
    ];
}
