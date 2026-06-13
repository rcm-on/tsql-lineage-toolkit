using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Regression suite over known lineage patterns: each test feeds a small
/// synthetic CREATE PROCEDURE/TABLE definition through SqlAnalyzer/TableAnalyzer
/// + GraphExporter and asserts on the resulting nodes/relationships, so a
/// future change that breaks alias resolution, CTE handling, column extraction,
/// dynamic-SQL tracking, etc. fails loudly here instead of silently degrading
/// the real graphs.
/// </summary>
public class LineageTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql, bool includeColumns = true, string objectName = "dbo.TestProc")
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns);
    }

    /// <summary>Like BuildGraph, but with a "{Database}::schema.table" -> column names lookup, mimicking CREATE TABLE schemas loaded from input.json.</summary>
    private static GraphPayload BuildGraphWithSchemas(string sql, IReadOnlyDictionary<string, List<string>> tableColumns, string objectName = "dbo.TestProc")
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql, tableColumns);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    private static GraphNode? FindNode(GraphPayload graph, Func<GraphNode, bool> pred) =>
        graph.Nodes.FirstOrDefault(pred);

    [Fact]
    public void InsertWithColumnList_WritesTargetAndColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1, Col2)
                SELECT SourceCol1, SourceCol2
                FROM dbo.Source
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);

        var writesTo = FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id);
        Assert.NotNull(writesTo);

        var writtenCols = graph.Relationships
            .Where(r => r.Type == "WRITES_COLUMN" && r.StartNodeId == writesTo!.StartNodeId)
            .Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"])
            .ToHashSet();
        Assert.Equal(new HashSet<object> { "Col1", "Col2" }, writtenCols);

        // INSERT...SELECT's source table also gets its own READS_FROM from the
        // same step (in addition to the WRITES_TO target above) - and
        // DERIVES_FROM still pairs each target column with its source column below.
        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == writesTo.StartNodeId && r.EndNodeId == source.Id));

        var col1 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Col1");
        var col2 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Col2");
        var srcCol1 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "SourceCol1");
        var srcCol2 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "SourceCol2");
        Assert.NotNull(col1);
        Assert.NotNull(col2);
        Assert.NotNull(srcCol1);
        Assert.NotNull(srcCol2);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == col1.Id && r.EndNodeId == srcCol1.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == col2.Id && r.EndNodeId == srcCol2.Id));
    }

    [Fact]
    public void InsertSelectStar_WritesTargetWithNoColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target
                SELECT * FROM dbo.Source
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);

        var writesTo = FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id);
        Assert.NotNull(writesTo);

        var writtenCols = graph.Relationships.Where(r => r.Type == "WRITES_COLUMN" && r.StartNodeId == writesTo!.StartNodeId);
        Assert.Empty(writtenCols);

        // "SELECT *" has no per-element expressions to pair with target columns,
        // so no DERIVES_FROM edges are produced (regardless of target schema).
        Assert.DoesNotContain(graph.Relationships, r => r.Type == "DERIVES_FROM");
    }

    [Fact]
    public void InsertSelectWithExpression_DerivesFromMultipleSourceColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Total)
                SELECT Quantity * UnitPrice
                FROM dbo.Source
            END
            """;
        var graph = BuildGraph(sql);

        var totalCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Total");
        var qtyCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Quantity");
        var priceCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "UnitPrice");
        Assert.NotNull(totalCol);
        Assert.NotNull(qtyCol);
        Assert.NotNull(priceCol);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol.Id && r.EndNodeId == qtyCol.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol.Id && r.EndNodeId == priceCol.Id));
    }

    [Fact]
    public void SelectFromTable_ReadsFromAndReadsColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT SourceCol1, SourceCol2 FROM dbo.Source
            END
            """;
        var graph = BuildGraph(sql);

        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);

        var readsFrom = FindRel(graph, "READS_FROM", r => r.EndNodeId == source!.Id);
        Assert.NotNull(readsFrom);

        var readCols = graph.Relationships
            .Where(r => r.Type == "READS_COLUMN" && r.StartNodeId == readsFrom!.StartNodeId)
            .Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"])
            .ToHashSet();
        Assert.Equal(new HashSet<object> { "SourceCol1", "SourceCol2" }, readCols);
    }

    [Fact]
    public void SelectStar_WithKnownSchema_ExpandsToAllColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT * FROM dbo.Source
            END
            """;
        var tableColumns = new Dictionary<string, List<string>>
        {
            ["TestDb::dbo.source"] = new List<string> { "Id", "Name", "Value" },
        };
        var graph = BuildGraphWithSchemas(sql, tableColumns);

        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);

        var readsFrom = FindRel(graph, "READS_FROM", r => r.EndNodeId == source!.Id);
        Assert.NotNull(readsFrom);

        var readCols = graph.Relationships
            .Where(r => r.Type == "READS_COLUMN" && r.StartNodeId == readsFrom!.StartNodeId)
            .Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"])
            .ToHashSet();
        Assert.Equal(new HashSet<object> { "Id", "Name", "Value" }, readCols);
    }

    [Fact]
    public void InsertWithoutColumnList_WithKnownSchema_ExpandsToAllTargetColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target
                SELECT * FROM dbo.Source
            END
            """;
        var tableColumns = new Dictionary<string, List<string>>
        {
            ["TestDb::dbo.target"] = new List<string> { "Id", "Name", "Value" },
        };
        var graph = BuildGraphWithSchemas(sql, tableColumns);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);

        var writesTo = FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id);
        Assert.NotNull(writesTo);

        var writtenCols = graph.Relationships
            .Where(r => r.Type == "WRITES_COLUMN" && r.StartNodeId == writesTo!.StartNodeId)
            .Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"])
            .ToHashSet();
        Assert.Equal(new HashSet<object> { "Id", "Name", "Value" }, writtenCols);
    }

    [Fact]
    public void UpdateFromAlias_ResolvesToRealTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE t
                SET t.Status = 'X'
                FROM dbo.Target t
                INNER JOIN dbo.Source s ON s.Id = t.Id
                WHERE s.Flag = 1
            END
            """;
        var graph = BuildGraph(sql);

        // The UPDATE target is the alias "t" - it must resolve back to dbo.Target,
        // not be left as a meaningless "t" table node.
        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "t"));

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);

        var writesTo = FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id);
        Assert.NotNull(writesTo);

        var writtenCols = graph.Relationships
            .Where(r => r.Type == "WRITES_COLUMN" && r.StartNodeId == writesTo!.StartNodeId)
            .Select(r => graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"]);
        Assert.Contains("Status", writtenCols);
    }

    [Fact]
    public void DeleteFromAlias_ResolvesToRealTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DELETE t
                FROM dbo.Target t
                INNER JOIN dbo.Source s ON s.Id = t.Id
                WHERE s.Flag = 1
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "t"));

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id));
    }

    [Fact]
    public void CteAlias_IsNotEmittedAsTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                ;WITH CteA AS (
                    SELECT Id FROM dbo.Source
                )
                SELECT Id FROM CteA
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "CteA"));

        // The top-level SELECT FROM CteA is resolved through the CTE to its real
        // base table, dbo.Source - CTEs are transparent for lineage purposes.
        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.EndNodeId == source.Id));

        var idCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Id");
        Assert.NotNull(idCol);
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.EndNodeId == idCol.Id));
    }

    [Fact]
    public void Merge_WritesToTarget()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                MERGE INTO dbo.Target AS t
                USING dbo.Source AS s ON t.Id = s.Id
                WHEN MATCHED THEN UPDATE SET t.Val = s.Val
                WHEN NOT MATCHED THEN INSERT (Id, Val) VALUES (s.Id, s.Val);
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id));
    }

    [Fact]
    public void DynamicSql_TracksBuildsSqlFromVariable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @TableName NVARCHAR(128)
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                SET @sql = 'SELECT * FROM ' + @TableName
                EXEC (@sql)
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        Assert.Equal(1, result.DynamicSqlCount);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "EXEC");
        Assert.NotNull(step);
        Assert.True((bool)step!.Properties["is_dynamic_sql"]);

        var varNode = FindNode(graph, n => n.Labels.Contains("Variable") && (string)n.Properties["name"] == "@sql");
        Assert.NotNull(varNode);

        Assert.NotNull(FindRel(graph, "BUILDS_SQL_FROM", r => r.StartNodeId == step!.Id && r.EndNodeId == varNode!.Id));
    }

    [Fact]
    public void TableVariable_IsNotEmittedAsTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @TempTable TABLE (Id INT)
                INSERT INTO @TempTable (Id)
                SELECT Id FROM dbo.Source
            END
            """;
        var graph = BuildGraph(sql);

        // No fake table node should be created for the table variable.
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("TempTable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecCall_ProducesCallsEdge()
    {
        var callerSql = """
            CREATE PROCEDURE dbo.A
            AS
            BEGIN
                EXEC dbo.B
            END
            """;
        var calleeSql = """
            CREATE PROCEDURE dbo.B
            AS
            BEGIN
                SELECT 1
            END
            """;

        var resultA = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.A", callerSql);
        var resultB = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.B", calleeSql);
        Assert.Null(resultA.Error);
        Assert.Null(resultB.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { resultA, resultB });

        Assert.NotNull(FindRel(graph, "CALLS",
            r => r.StartNodeId == $"{Db}::dbo.A" && r.EndNodeId == $"{Db}::dbo.B"));
    }

    [Fact]
    public void NestedIf_ProducesNestedRulesAndGoverns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Flag INT, @Other INT
            AS
            BEGIN
                IF @Flag = 1
                BEGIN
                    IF @Other = 2
                        UPDATE dbo.Target SET Col1 = 1
                END
            END
            """;
        var graph = BuildGraph(sql);

        var rules = graph.Nodes.Where(n => n.Labels.Contains("Rule")).ToList();
        Assert.Equal(2, rules.Count);

        // Innermost rule GOVERNS the UPDATE step.
        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "UPDATE");
        Assert.NotNull(step);

        var governs = FindRel(graph, "GOVERNS", r => r.EndNodeId == step!.Id);
        Assert.NotNull(governs);

        // The governing rule is itself NESTED_IN the outer rule.
        Assert.NotNull(FindRel(graph, "NESTED_IN", r => r.StartNodeId == governs!.StartNodeId));
    }

    [Fact]
    public void TransactionCursorErrorHandling_FlagsAreSet()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                BEGIN TRY
                    BEGIN TRANSACTION
                    DECLARE cur CURSOR FOR SELECT Id FROM dbo.Source
                    COMMIT
                END TRY
                BEGIN CATCH
                    ROLLBACK
                END CATCH
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        Assert.True(result.HasTransaction);
        Assert.True(result.HasErrorHandling);
        Assert.True(result.HasCursor);
    }

    [Fact]
    public void CreateTable_ColumnsAndForeignKeys_ProduceFkToAndReferences()
    {
        var customersSql = """
            CREATE TABLE dbo.Customers
            (
                Id INT NOT NULL IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL
            )
            """;
        var ordersSql = """
            CREATE TABLE dbo.Orders
            (
                Id INT NOT NULL IDENTITY(1,1) PRIMARY KEY,
                CustomerId INT NOT NULL,
                CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id)
            )
            """;

        var customers = TableAnalyzer.AnalyzeTable($"{Db}::dbo.Customers", customersSql);
        var orders = TableAnalyzer.AnalyzeTable($"{Db}::dbo.Orders", ordersSql);
        Assert.Null(customers.Error);
        Assert.Null(orders.Error);

        var graph = GraphExporter.Build(new List<ObjectResult>(), includeColumns: true,
            tableSchemas: new List<TableSchemaResult> { customers, orders });

        var customersTable = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Customers");
        var ordersTable = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Orders");
        Assert.NotNull(customersTable);
        Assert.NotNull(ordersTable);

        var idCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.customers" && (string)n.Properties["name"] == "Id");
        Assert.NotNull(idCol);
        Assert.True((bool)idCol!.Properties["is_primary_key"]);
        Assert.True((bool)idCol.Properties["is_identity"]);
        Assert.Equal(1, idCol.Properties["ordinal"]);

        Assert.NotNull(FindRel(graph, "FK_TO",
            r => r.StartNodeId == ordersTable!.Id && r.EndNodeId == customersTable!.Id
                 && (string)r.Properties["constraint"] == "FK_Orders_Customers"));

        var customerIdCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "CustomerId");
        Assert.NotNull(FindRel(graph, "REFERENCES",
            r => r.StartNodeId == customerIdCol!.Id && r.EndNodeId == idCol.Id));
    }

    [Fact]
    public void SelectAssignsVariable_ProducesAssignedFromColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Id INT
            AS
            BEGIN
                DECLARE @Status VARCHAR(20)
                SELECT @Status = Status FROM dbo.Source WHERE Id = @Id
            END
            """;
        var graph = BuildGraph(sql);

        var varNode = FindNode(graph, n => n.Labels.Contains("Variable") && (string)n.Properties["name"] == "@Status");
        Assert.NotNull(varNode);

        var srcCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(srcCol);

        Assert.NotNull(FindRel(graph, "ASSIGNED_FROM", r => r.StartNodeId == varNode!.Id && r.EndNodeId == srcCol!.Id));
    }

    [Fact]
    public void SetVariableFromSubquery_ProducesAssignedFromColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Id INT
            AS
            BEGIN
                DECLARE @MaxQty INT
                SET @MaxQty = (SELECT MAX(Quantity) FROM dbo.Source WHERE Id = @Id)
            END
            """;
        var graph = BuildGraph(sql);

        var varNode = FindNode(graph, n => n.Labels.Contains("Variable") && (string)n.Properties["name"] == "@MaxQty");
        Assert.NotNull(varNode);

        var srcCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Quantity");
        Assert.NotNull(srcCol);

        Assert.NotNull(FindRel(graph, "ASSIGNED_FROM", r => r.StartNodeId == varNode!.Id && r.EndNodeId == srcCol!.Id));
    }

    [Fact]
    public void UpdateUsingVariable_ProducesUsesVariableEdge()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Status VARCHAR(20)
            AS
            BEGIN
                UPDATE dbo.Target
                SET Col1 = @Status
                WHERE Id = 1
            END
            """;
        var graph = BuildGraph(sql);

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "UPDATE");
        Assert.NotNull(step);

        var paramNode = FindNode(graph, n => n.Labels.Contains("Parameter") && (string)n.Properties["name"] == "@Status");
        Assert.NotNull(paramNode);

        Assert.NotNull(FindRel(graph, "USES_VARIABLE", r => r.StartNodeId == step!.Id && r.EndNodeId == paramNode!.Id));
    }

    /// <summary>
    /// "SELECT a.Col1, b.Col2 FROM A a JOIN B b ...": both JOIN sides get their own
    /// Table node, READS_FROM edge and READS_COLUMN (resolved via each column's
    /// "alias.Column" qualifier) - not just the first table after FROM.
    /// </summary>
    [Fact]
    public void SelectWithJoin_BothTablesGetReadsFromAndReadsColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT a.Col1, b.Col2 FROM dbo.A a JOIN dbo.B b ON a.Id = b.Id
            END
            """;
        var graph = BuildGraph(sql);

        var tableA = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.A");
        var tableB = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.B");
        Assert.NotNull(tableA);
        Assert.NotNull(tableB);

        var readsA = FindRel(graph, "READS_FROM", r => r.EndNodeId == tableA.Id);
        var readsB = FindRel(graph, "READS_FROM", r => r.EndNodeId == tableB.Id);
        Assert.NotNull(readsA);
        Assert.NotNull(readsB);
        Assert.Equal(readsA.StartNodeId, readsB.StartNodeId); // same step

        var col1 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.a" && (string)n.Properties["name"] == "Col1");
        var col2 = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.b" && (string)n.Properties["name"] == "Col2");
        Assert.NotNull(col1);
        Assert.NotNull(col2);

        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == readsA.StartNodeId && r.EndNodeId == col1.Id));
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == readsA.StartNodeId && r.EndNodeId == col2.Id));
    }

    /// <summary>
    /// "INSERT INTO T (Total) SELECT a.Qty * b.Price FROM A a JOIN B b ...": the
    /// expression mixes columns from both JOIN sides, so Total gets two DERIVES_FROM
    /// edges - one to A.Qty, one to B.Price - instead of mis-attributing both to A.
    /// </summary>
    [Fact]
    public void InsertSelectWithJoin_DerivesFromCorrectSourceTablePerColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Total)
                SELECT a.Qty * b.Price
                FROM dbo.A a
                JOIN dbo.B b ON a.Id = b.Id
            END
            """;
        var graph = BuildGraph(sql);

        var totalCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Total");
        var qtyCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.a" && (string)n.Properties["name"] == "Qty");
        var priceCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.b" && (string)n.Properties["name"] == "Price");
        Assert.NotNull(totalCol);
        Assert.NotNull(qtyCol);
        Assert.NotNull(priceCol);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol.Id && r.EndNodeId == qtyCol.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol.Id && r.EndNodeId == priceCol.Id));
    }

    /// <summary>
    /// Documents a known limitation: InsertSelectLineage requires the INSERT source
    /// to be a single QuerySpecification, so "SELECT ... UNION ALL SELECT ..." (a
    /// BinaryQueryExpression) produces no DERIVES_FROM edges for either branch -
    /// the WRITES_TO/WRITES_COLUMN edges for the target are unaffected.
    /// </summary>
    [Fact]
    public void InsertSelectWithUnion_ProducesNoColumnLineage()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT Col1 FROM dbo.Source1
                UNION ALL
                SELECT Col1 FROM dbo.Source2
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target.Id));

        Assert.DoesNotContain(graph.Relationships, r => r.Type == "DERIVES_FROM");
    }

    [Fact]
    public void DumpInsertUnionGraph_WritesJson()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Col1)
                SELECT Col1 FROM dbo.Source1
                UNION ALL
                SELECT Col1 FROM dbo.Source2
            END
            """;

        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var outObj = Path.Combine(Directory.GetCurrentDirectory(), "union_test_object.json");
        var outGraph = Path.Combine(Directory.GetCurrentDirectory(), "union_test_graph.json");
        File.WriteAllText(outObj, JsonSerializer.Serialize(result, options));
        File.WriteAllText(outGraph, JsonSerializer.Serialize(graph, options));

        // Confirm expected test condition programmatically as well
        Assert.DoesNotContain(graph.Relationships, r => r.Type == "DERIVES_FROM");
    }

    /// <summary>
    /// The Graphify exporter is a lossless reshape of the Neo4j-shaped graph:
    /// same node/edge counts, every node keeps its id/labels/properties and gains
    /// a single "type" (its most specific label) and a display "label", every edge
    /// becomes source/target/type with its properties preserved. Verified here plus
    /// a dump to disk so the on-disk shape can be eyeballed / fed to Graphify.
    /// </summary>
    [Fact]
    public void GraphifyExporter_ReshapesGraphLosslessly()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Total)
                SELECT a.Qty * b.Price
                FROM dbo.A a
                JOIN dbo.B b ON a.Id = b.Id
            END
            """;
        var graph = BuildGraph(sql);
        var graphify = GraphifyExporter.ToGraphify(graph, Db);

        // Lossless: one graphify node/edge per source node/relationship.
        Assert.Equal(graph.Nodes.Count, graphify.Nodes.Count);
        Assert.Equal(graph.Relationships.Count, graphify.Edges.Count);

        // A Table node keeps its id, carries type "Table", a human label, and the
        // flat size/color Graphify needs to render it - plus its properties flattened
        // to the top level (e.g. "name" sits directly on the node, not under a nested
        // "properties" object).
        var tableNode = graphify.Nodes.First(n => (string)n["type"] == "Table" && (string)n["name"] == "dbo.A");
        Assert.Equal("dbo.A", (string)tableNode["label"]);
        Assert.True(tableNode.ContainsKey("size"));
        Assert.True(tableNode.ContainsKey("color"));

        // Every edge points at real node ids and keeps its relationship type + a color.
        var nodeIds = graphify.Nodes.Select(n => (string)n["id"]).ToHashSet();
        Assert.All(graphify.Edges, e =>
        {
            Assert.Contains((string)e["source"], nodeIds);
            Assert.Contains((string)e["target"], nodeIds);
            Assert.True(e.ContainsKey("color"));
        });
        Assert.Contains(graphify.Edges, e => (string)e["type"] == "DERIVES_FROM");

        var options = new JsonSerializerOptions { WriteIndented = true };
        var outPath = Path.Combine(Directory.GetCurrentDirectory(), "graphify_sample.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(graphify, options));
    }

    /// <summary>
    /// GraphML export is well-formed XML carrying the same nodes/edges, with one
    /// &lt;node&gt;/&lt;edge&gt; per graph node/relationship and the synthetic
    /// ntype/etype attributes for type-based coloring in Gephi/yEd.
    /// </summary>
    [Fact]
    public void GraphMlExporter_ProducesWellFormedXmlWithNodesAndEdges()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Total)
                SELECT a.Qty * b.Price
                FROM dbo.A a
                JOIN dbo.B b ON a.Id = b.Id
            END
            """;
        var graph = BuildGraph(sql);
        var xml = GraphMlExporter.ToGraphMl(graph);

        // Parses as XML (well-formed) and is GraphML.
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
        Assert.Equal("graphml", doc.Root!.Name.LocalName);

        var nodeEls = doc.Descendants(ns + "node").ToList();
        var edgeEls = doc.Descendants(ns + "edge").ToList();
        Assert.Equal(graph.Nodes.Count, nodeEls.Count);
        Assert.Equal(graph.Relationships.Count, edgeEls.Count);

        // Edge source/target reference real node ids, and DERIVES_FROM is present.
        var nodeIds = nodeEls.Select(n => (string)n.Attribute("id")!).ToHashSet();
        Assert.All(edgeEls, e =>
        {
            Assert.Contains((string)e.Attribute("source")!, nodeIds);
            Assert.Contains((string)e.Attribute("target")!, nodeIds);
        });
        Assert.Contains(edgeEls, e => e.Elements(ns + "data").Any(d => (string)d.Attribute("key")! == "etype" && d.Value == "DERIVES_FROM"));

        var outPath = Path.Combine(Directory.GetCurrentDirectory(), "graphml_sample.graphml");
        File.WriteAllText(outPath, xml);
    }
}
