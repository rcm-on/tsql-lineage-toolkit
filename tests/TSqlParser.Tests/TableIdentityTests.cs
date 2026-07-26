using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Regression tests for a real false negative found in Ola Hallengren's maintenance
/// solution (docs/... table-identity writeup): "UPDATE alias SET ... FROM (subquery)
/// AS alias" - where alias equals a real table's short name - used to mint a SECOND
/// :Table node ("QueueDatabase") distinct from the one every other qualified reference
/// ("dbo.QueueDatabase") already used, silently splitting a table's reads from its
/// writes across two nodes. "who writes dbo.QueueDatabase?" then answered zero, while
/// three procedures actually did.
///
/// Two independent root causes, both covered here:
///  1. AstWalker.ResolveAlias didn't handle a QueryDerivedTable (subquery) alias, so
///     TargetName fell back to the bare literal instead of resolving through the
///     derived table to its one real base table.
///  2. TableAnalyzer/InputAnalyzer only recognized a CREATE TABLE at the top level of
///     a batch, missing the common idempotent-install "IF NOT EXISTS(...) BEGIN CREATE
///     TABLE ... END" pattern - so the table's schema was never registered at all.
/// A third, general fix (GraphExporter.GetOrCreateTable's short-name index) makes any
/// remaining unqualified reference to an already-known qualified table resolve to the
/// same node, rather than requiring every alias shape to be special-cased in AstWalker.
/// </summary>
public class TableIdentityTests
{
    private const string Db = "TestDb";

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    private static GraphNode? FindNode(GraphPayload graph, Func<GraphNode, bool> pred) =>
        graph.Nodes.FirstOrDefault(pred);

    [Fact]
    public void UpdateAliasFromDerivedTable_SameShortNameAsTable_SharesOneTableNode()
    {
        // Same shape as Ola Hallengren's DatabaseBackup.sql:
        //   UPDATE QueueDatabase SET ... FROM (SELECT TOP 1 ... FROM dbo.QueueDatabase ...) QueueDatabase
        // "QueueDatabase" (no schema) is both the UPDATE target AND the alias given to
        // a derived table whose own FROM reads the real, schema-qualified table.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT DatabaseName FROM dbo.QueueDatabase WHERE QueueID = 1;

                UPDATE QueueDatabase
                SET DatabaseStartTime = SYSDATETIME()
                FROM (SELECT TOP 1 DatabaseStartTime, DatabaseName
                      FROM dbo.QueueDatabase
                      WHERE QueueID = 1
                      ) QueueDatabase
            END
            """;

        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: false);

        var tableNodes = graph.Nodes
            .Where(n => n.Labels.Contains("Table")
                     && SqlText.NormalizeRef((string)n.Properties["name"]) == "dbo.queuedatabase")
            .ToList();
        Assert.True(tableNodes.Count == 1,
            $"expected exactly one dbo.QueueDatabase Table node, found {tableNodes.Count}: " +
            string.Join(", ", tableNodes.Select(n => n.Id)));
        var tableId = tableNodes[0].Id;

        var readStep = $"{Db}::dbo.TestProc#step0";
        var writeStep = $"{Db}::dbo.TestProc#step1";

        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == readStep && r.EndNodeId == tableId));
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.StartNodeId == writeStep && r.EndNodeId == tableId));
    }

    [Fact]
    public void IfNotExistsGuardedCreateTable_IsRecognizedWithColumns()
    {
        // The common idempotent-install pattern used throughout Ola Hallengren's scripts
        // (CommandLog.sql, Queue.sql, QueueDatabase.sql): the CREATE TABLE is nested
        // inside an IF guard, not a top-level batch statement.
        var sql = """
            IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[QueueDatabase]') AND type = 'U')
            BEGIN
            CREATE TABLE [dbo].[QueueDatabase]
            (
                [ID] [int] IDENTITY(1,1) NOT NULL,
                [QueueID] [int] NOT NULL,
                [DatabaseName] [nvarchar](256) NOT NULL,
                CONSTRAINT [PK_QueueDatabase] PRIMARY KEY CLUSTERED ([ID] ASC)
            )
            END
            """;

        var result = TableAnalyzer.AnalyzeTable($"{Db}::dbo.QueueDatabase", sql);

        Assert.Null(result.Error);
        Assert.Contains(result.Columns, c => c.Name == "QueueID");
        Assert.Contains(result.Columns, c => c.Name == "DatabaseName");
        var idCol = result.Columns.Single(c => c.Name == "ID");
        Assert.True(idCol.IsPrimaryKey);
        Assert.True(idCol.IsIdentity);

        // Also drives the router: a file shaped like this must land in TableSchemas,
        // not be misrouted to SqlAnalyzer (which would report it as object_type UNKNOWN
        // with zero columns, and never register it for unqualified-reference resolution).
        Assert.True(TableAnalyzer.LooksLikeTableScript(sql));
    }

    [Fact]
    public void UnqualifiedReferenceToKnownTable_ResolvesToQualifiedNode()
    {
        var createSql = """
            IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Widgets]') AND type = 'U')
            BEGIN
            CREATE TABLE dbo.Widgets
            (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL
            )
            END
            """;
        var tableSchema = TableAnalyzer.AnalyzeTable($"{Db}::dbo.Widgets", createSql);
        Assert.Null(tableSchema.Error);

        // A plain, unqualified reference - no schema prefix, no alias tricks - to the
        // known table.
        var readerSql = """
            CREATE PROCEDURE dbo.ReadWidgets
            AS
            BEGIN
                SELECT Name FROM Widgets WHERE Id = 1
            END
            """;
        var reader = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.ReadWidgets", readerSql);
        Assert.Null(reader.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { reader }, includeColumns: false,
            tableSchemas: new List<TableSchemaResult> { tableSchema });

        var tableNodes = graph.Nodes
            .Where(n => n.Labels.Contains("Table")
                     && SqlText.NormalizeRef((string)n.Properties["name"]) == "dbo.widgets")
            .ToList();
        Assert.True(tableNodes.Count == 1,
            $"expected exactly one dbo.Widgets Table node, found {tableNodes.Count}: " +
            string.Join(", ", tableNodes.Select(n => n.Id)));

        var readStep = $"{Db}::dbo.ReadWidgets#step0";
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == readStep && r.EndNodeId == tableNodes[0].Id));
    }
}
