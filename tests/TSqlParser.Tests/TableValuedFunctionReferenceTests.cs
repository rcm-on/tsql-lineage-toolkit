using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Regression tests for a real false negative: every reference to a table-valued
/// function (TVF) - user-defined ("dbo.MiFuncionTabla(1)"), catalog
/// ("sys.dm_io_virtual_file_stats(...)"), or a built-in table-shaping function
/// (OPENJSON/OPENQUERY/OPENROWSET/STRING_SPLIT) - used to silently vanish from the
/// lineage graph. AstWalker.CollectTableRefsInto's switch covered NamedTableReference,
/// QualifiedJoin/UnqualifiedJoin, QueryDerivedTable, Pivoted/UnpivotedTableReference,
/// but fell through to nothing for SchemaObjectFunctionTableReference and every
/// built-in table-function AST node - no error, no edge, just a hole in the graph.
///
/// Two different fixes land here, matching two different node identities:
///  1. A user-defined TVF (SchemaObjectFunctionTableReference outside "sys") already
///     exists in the graph as its own SqlObject (INLINE_TABLE_FUNCTION) - it gets a
///     CALLS edge (object-level, via AstWalker.FunctionCallCollector /
///     GraphExporter's FunctionCalls resolution, both pre-existing) instead of a
///     second, twin :Table node. CollectTableRefsInto deliberately does NOT add it to
///     the table-refs list.
///  2. Everything else here - catalog TVFs (sys.dm_*), OPENJSON, OPENQUERY,
///     OPENROWSET(provider, connString, object), STRING_SPLIT/GENERATE_SERIES-style
///     built-ins - is never an analyzed SqlObject, so it gets registered as a plain
///     (symbolic, for the built-ins) table reference and flows through the existing
///     READS_FROM/GetOrCreateTable machinery, same as "sys.databases" always did.
/// </summary>
public class TableValuedFunctionReferenceTests
{
    private const string Db = "TestDb";

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    private static List<GraphRel> FindRels(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.Where(r => r.Type == type && (extra == null || extra(r))).ToList();

    private static GraphNode? FindNode(GraphPayload graph, Func<GraphNode, bool> pred) =>
        graph.Nodes.FirstOrDefault(pred);

    // ── 1. User-defined TVF: FROM dbo.MiFuncionTabla(1) reaches the graph ──────────

    [Fact]
    public void UserDefinedTvf_ReferencedInFrom_GetsCallsEdgeToTheFunctionObject()
    {
        var fnSql = """
            CREATE FUNCTION dbo.MiFuncionTabla(@x INT)
            RETURNS TABLE
            AS
            RETURN (SELECT 1 AS Col1);
            """;
        var procSql = """
            CREATE PROCEDURE dbo.PruebaTvf AS
            BEGIN
                SELECT * FROM dbo.MiFuncionTabla(1);
            END
            """;

        var fnResult = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.MiFuncionTabla", fnSql);
        var procResult = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.PruebaTvf", procSql);
        Assert.Null(fnResult.Error);
        Assert.Null(procResult.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { fnResult, procResult }, includeColumns: false);

        Assert.NotNull(FindRel(graph, "CALLS",
            r => r.StartNodeId == $"{Db}::dbo.PruebaTvf" && r.EndNodeId == $"{Db}::dbo.MiFuncionTabla"));
    }

    // ── 4. Control: a TVF that ALSO exists as a SqlObject creates no twin :Table ────

    [Fact]
    public void UserDefinedTvf_KnownAsSqlObject_CreatesNoTwinTableNode()
    {
        var fnSql = """
            CREATE FUNCTION dbo.MiFuncionTabla(@x INT)
            RETURNS TABLE
            AS
            RETURN (SELECT 1 AS Col1);
            """;
        var procSql = """
            CREATE PROCEDURE dbo.PruebaTvf AS
            BEGIN
                SELECT * FROM dbo.MiFuncionTabla(1);
            END
            """;

        var fnResult = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.MiFuncionTabla", fnSql);
        var procResult = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.PruebaTvf", procSql);
        var graph = GraphExporter.Build(new List<ObjectResult> { fnResult, procResult }, includeColumns: false);

        // No :Table node named after the function - the CALLS edge above is the only
        // trace of the reference; a :Table twin would silently split reads/impact
        // between "the function" (SqlObject) and "the table with the same name".
        var twin = FindNode(graph, n => n.Labels.Contains("Table")
            && SqlText.NormalizeRef((string)n.Properties["name"]) == "dbo.mifuncionTabla".ToLowerInvariant());
        Assert.Null(twin);

        // And no READS_FROM at all off the step for this reference (it's CALLS-only).
        Assert.Null(FindRel(graph, "READS_FROM",
            r => r.StartNodeId == $"{Db}::dbo.PruebaTvf#step0"));
    }

    // ── 2. Catalog TVF: FROM sys.dm_exec_sql_text(0x0) reaches the graph ───────────

    [Fact]
    public void CatalogTvf_ReferencedInFrom_GetsReadsFromEdge()
    {
        var sql = """
            CREATE PROCEDURE dbo.PruebaDmv AS
            BEGIN
                SELECT * FROM sys.dm_exec_sql_text(0x0);
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.PruebaDmv", sql);
        Assert.Null(result.Error);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: false);

        var tableNode = FindNode(graph, n => n.Labels.Contains("Table")
            && SqlText.NormalizeRef((string)n.Properties["name"]) == "sys.dm_exec_sql_text");
        Assert.NotNull(tableNode);
        Assert.NotNull(FindRel(graph, "READS_FROM",
            r => r.StartNodeId == $"{Db}::dbo.PruebaDmv#step0" && r.EndNodeId == tableNode!.Id));
    }

    // ── 3. JOIN: both a real table and a catalog TVF get registered ────────────────

    [Fact]
    public void JoinOfTableAndCatalogTvf_BothRegistered()
    {
        var sql = """
            CREATE PROCEDURE dbo.PruebaJoin AS
            BEGIN
                SELECT * FROM dbo.Clientes c JOIN sys.dm_os_volume_stats(1,1) v ON 1=1;
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.PruebaJoin", sql);
        Assert.Null(result.Error);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: false);

        var stepId = $"{Db}::dbo.PruebaJoin#step0";
        var clientesNode = FindNode(graph, n => n.Labels.Contains("Table")
            && SqlText.NormalizeRef((string)n.Properties["name"]) == "dbo.clientes");
        var dmvNode = FindNode(graph, n => n.Labels.Contains("Table")
            && SqlText.NormalizeRef((string)n.Properties["name"]) == "sys.dm_os_volume_stats");

        Assert.NotNull(clientesNode);
        Assert.NotNull(dmvNode);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == stepId && r.EndNodeId == clientesNode!.Id));
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == stepId && r.EndNodeId == dmvNode!.Id));
    }

    // ── OPENJSON: WideWorldImporters' Website.RecordVehicleTemperature shape ───────

    [Fact]
    public void InsertSelectFromOpenJsonWithClause_GetsReadsFromAndColumnLineage()
    {
        // Same shape as WideWorldImporters' Website.RecordVehicleTemperature: an
        // INSERT...SELECT off "OPENJSON(@param, path) WITH (...)" - before this fix,
        // CollectTableRefsInto returned zero table refs for this FROM clause, so
        // InsertSelectLineage bailed out entirely (tableRefs.Count == 0) - the object
        // had exactly one data edge (WRITES_TO) and zero READS_FROM/column lineage.
        var sql = """
            CREATE PROCEDURE Website.RecordVehicleTemperature
                @FullSensorDataArray nvarchar(1000)
            AS
            BEGIN
                INSERT Warehouse.VehicleTemperatures
                    (VehicleRegistration, ChillerSensorNumber, Temperature)
                SELECT VehicleRegistration, ChillerSensorNumber, Temperature
                FROM OPENJSON(@FullSensorDataArray, N'$.Recordings')
                WITH ( VehicleRegistration nvarchar(40) N'$.properties.rego',
                       ChillerSensorNumber int N'$.properties.sensor',
                       Temperature decimal(18,2) N'$.properties.temp');
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::Website.RecordVehicleTemperature", sql);
        Assert.Null(result.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        var dataEdges = graph.Relationships
            .Where(r => r.Type is "READS_FROM" or "WRITES_TO")
            .ToList();
        Assert.True(dataEdges.Count > 1,
            $"expected more than the pre-fix single WRITES_TO edge, found {dataEdges.Count}");

        Assert.NotNull(FindRel(graph, "READS_FROM",
            r => r.StartNodeId == $"{Db}::Website.RecordVehicleTemperature#step0"
              && ((string)graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"]).StartsWith("OPENJSON(", StringComparison.Ordinal)));

        // The WITH-declared names double as the outer SELECT's unqualified column
        // list, so the existing single-table column-attribution shortcut gives real
        // column-level lineage for free, without parsing the WITH clause's types/paths.
        var derives = FindRels(graph, "DERIVES_FROM");
        Assert.Contains(derives, r => r.StartNodeId.EndsWith(":column:VehicleRegistration", StringComparison.Ordinal));
        Assert.Contains(derives, r => r.StartNodeId.EndsWith(":column:ChillerSensorNumber", StringComparison.Ordinal));
        Assert.Contains(derives, r => r.StartNodeId.EndsWith(":column:Temperature", StringComparison.Ordinal));
    }
}
