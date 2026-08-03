using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// "INSERT INTO T (...) SELECT ... FROM S WHERE ..." never got a FILTERS_ON edge for
/// its WHERE clause: AddLink's own auto-detect (ExtractFilterColumns) only recognizes
/// SELECT/UPDATE/DELETE statements, and the WHERE lives on the SelectInsertSource
/// nested inside the InsertStatement, not on the InsertStatement itself. A WHERE-nested
/// EXISTS/IN subquery's own table (e.g. "WHERE EXISTS (SELECT 1 FROM dbo.Blocked b
/// WHERE b.Id = s.Id)") was silently dropped from READS_FROM entirely, since it only
/// ever surfaced through the same missing filter-column extraction. These lock in the
/// fix: InsertSelectLineage now runs the same ExtractFilterColumnsCore used by every
/// other statement kind and passes it to AddLink as an override.
/// </summary>
public class InsertSelectFilterTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql)
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static bool FiltersOn(GraphPayload g, string table, string column) =>
        g.Relationships.Any(r => r.Type == "FILTERS_ON" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId &&
                n.Labels.Contains("Column") &&
                string.Equals((string)n.Properties["table"], table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)n.Properties["name"], column, StringComparison.OrdinalIgnoreCase)));

    private static bool ReadsFrom(GraphPayload g, string table) =>
        g.Relationships.Any(r => r.Type == "READS_FROM" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId &&
                n.Labels.Contains("Table") &&
                string.Equals((string)n.Properties["name"], table, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void InsertSelect_WhereClause_ProducesFiltersOn()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT Col1 FROM dbo.Source WHERE Source.Activo = 1
            END
            """);
        Assert.True(FiltersOn(g, "dbo.source", "Activo"), "el WHERE de un INSERT...SELECT debe producir FILTERS_ON");
    }

    [Fact]
    public void InsertSelect_JoinOnClause_ProducesFiltersOn()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT a.Col1 FROM dbo.A a JOIN dbo.B b ON a.Id = b.Id AND b.Borrado = 0
            END
            """);
        Assert.True(FiltersOn(g, "dbo.b", "Borrado"), "el ON de un JOIN dentro de INSERT...SELECT debe producir FILTERS_ON");
    }

    [Fact]
    public void InsertSelect_WhereExists_ReadsNestedSubqueryTable()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT s.Col1 FROM dbo.Source s
                WHERE EXISTS (SELECT 1 FROM dbo.Blocked b WHERE b.Id = s.Id)
            END
            """);
        Assert.True(ReadsFrom(g, "dbo.Source"), "la tabla principal del INSERT...SELECT debe seguir leyendose");
        Assert.True(ReadsFrom(g, "dbo.Blocked"), "la tabla anidada en el EXISTS del WHERE debe producir su propio READS_FROM");
    }

    [Fact]
    public void InsertSelect_WhereIn_ReadsNestedSubqueryTable()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT s.Col1 FROM dbo.Source s
                WHERE s.Id IN (SELECT Id FROM dbo.Allowed)
            END
            """);
        Assert.True(ReadsFrom(g, "dbo.Allowed"), "la tabla anidada en el IN del WHERE debe producir su propio READS_FROM");
    }

    /// <summary>
    /// Control: a transient target (table variable) drops its DERIVES_FROM column
    /// lineage on purpose (it is not a real, addressable object), but the primary
    /// FROM table's own read columns must still be attributed - previously only the
    /// JOINed table (a genuine "extra") survived, and the primary table's columns
    /// (only ever mentioned inside the SELECT list expression) were lost entirely.
    /// </summary>
    [Fact]
    public void InsertSelectIntoTableVariable_StillReadsPrimaryTableColumns()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @tmp TABLE (Total INT);
                INSERT INTO @tmp (Total)
                SELECT a.Qty * b.Price FROM dbo.A a JOIN dbo.B b ON a.Id = b.Id
            END
            """);
        var qtyCol = FindNode(g, n => n.Labels.Contains("Column") &&
            string.Equals((string)n.Properties["table"], "dbo.a", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)n.Properties["name"], "Qty", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(qtyCol);
        Assert.True(g.Relationships.Any(r => (r.Type == "READS_COLUMN" || r.Type == "FILTERS_ON") && r.EndNodeId == qtyCol!.Id),
            "la tabla principal (A) del INSERT...SELECT hacia una variable tabla debe seguir atribuyendo sus columnas leidas");
    }

    private static GraphNode? FindNode(GraphPayload g, Func<GraphNode, bool> pred) => g.Nodes.FirstOrDefault(pred);
}
