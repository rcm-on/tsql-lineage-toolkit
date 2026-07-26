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

    // The following four tests pin the "UPDATE alias ... FROM real AS alias" /
    // "DELETE alias FROM real AS alias" resolution documented in
    // docs/frk-torture-report.md (sp_BlitzCache/sp_DatabaseRestore/sp_kill): the
    // bare alias from the UPDATE/DELETE must be resolved against the FROM clause to
    // the real target, and never leak out as a phantom :Table node named "b"/"fl"/"t".
    // (The plain real-table JOIN form is already covered by
    // UpdateFromAlias_ResolvesToRealTable / DeleteFromAlias_ResolvesToRealTable above.)

    [Fact]
    public void UpdateFromAliasedGlobalTemp_ResolvesToGlobalTemp_NoAliasNode()
    {
        // sp_BlitzCache.sql:3224 shape: UPDATE <alias> ... FROM #temp JOIN ##global <alias>.
        // The alias "b" must resolve to ##BlitzCacheProcs, which - like any #/##
        // temp - is not emitted as a real :Table node; the alias "b" must not be either.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE b
                SET b.compile_timeout = 1
                FROM #statements s
                JOIN ##BlitzCacheProcs b ON b.Id = s.Id
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "b"));
        // ## globals follow the same temp guard as # locals: no real :Table node.
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("BlitzCacheProcs", StringComparison.OrdinalIgnoreCase));
        // Consequently there is no WRITES_TO pointing at a phantom alias/global-temp node.
        Assert.DoesNotContain(graph.Relationships, r => r.Type == "WRITES_TO" &&
            (string)graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"] == "b");
    }

    [Fact]
    public void DeleteFromAliasedTableVariable_FollowsTableVariableConvention_NoAliasNode()
    {
        // sp_DatabaseRestore.sql:1389 shape: DELETE <alias> FROM @tablevar AS <alias>.
        // Table variables never become :Table nodes (see TableVariable_IsNotEmittedAsTable),
        // so resolving "fl" -> @FileList must follow that same rule: no "fl" node, no
        // "@FileList" node, and no WRITES_TO edge for either.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @FileList TABLE (BackupPath NVARCHAR(255), BackupFile NVARCHAR(255))
                DELETE fl
                FROM @FileList AS fl
                WHERE fl.BackupPath + fl.BackupFile <= 'z'
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "fl"));
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("FileList", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(graph.Relationships, r => r.Type == "WRITES_TO" &&
            (string)graph.Nodes.First(n => n.Id == r.EndNodeId).Properties["name"] == "fl");
    }

    [Fact]
    public void UpdateFromAliasedRealTable_JoinToTableVariable_ResolvesToRealTable()
    {
        // Mixed form: real table aliased, joined to a table variable - the UPDATE
        // alias must still resolve to the real base table (WRITES_TO dbo.Target),
        // and no phantom alias node appears.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @Ids TABLE (Id INT)
                UPDATE t
                SET t.Status = 'X'
                FROM dbo.Target AS t
                JOIN @Ids AS i ON i.Id = t.Id
            END
            """;
        var graph = BuildGraph(sql);

        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "t"));
        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id));
    }

    [Fact]
    public void UpdateRealTableWithoutFrom_Unchanged()
    {
        // Control: a plain "UPDATE dbo.T SET ..." with no FROM clause must be
        // unaffected by the alias-resolution path and still write dbo.T directly.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE dbo.T
                SET Reason = 'Lead blocker'
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.T");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id));
    }

    [Fact]
    public void Synonym_ReadThroughSynonym_ResolvesToBaseTable()
    {
        // A CREATE SYNONYM must not split impact analysis: a proc that reads through the
        // synonym must show up as a reader of the real base table, not of a phantom
        // ":table:dbo.synorders" node. Requires two objects (the synonym + its consumer).
        var synonym = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.synOrders", "CREATE SYNONYM dbo.synOrders FOR dbo.Orders;");
        Assert.Null(synonym.Error);
        Assert.Equal("SYNONYM", synonym.ObjectType);

        var reader = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT OrderId FROM dbo.synOrders;
            END
            """);
        Assert.Null(reader.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { synonym, reader }, includeColumns: true);

        // The phantom synonym :Table node is gone; the base table exists and is read.
        Assert.Null(FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.synOrders"));
        var orders = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Orders");
        Assert.NotNull(orders);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.EndNodeId == orders!.Id));

        // The synonym's SqlObject is retained and documents the alias.
        Assert.NotNull(FindRel(graph, "ALIAS_OF",
            r => r.StartNodeId == $"{Db}::dbo.synOrders" && r.EndNodeId == orders!.Id));
    }

    [Fact]
    public void Tvf_InvokedViaCrossApply_ProducesCallsEdge()
    {
        // A table-valued function invoked as a table source (CROSS APPLY dbo.tvf(...)) must
        // link the caller to the TVF with a CALLS edge, so impact reaches the TVF's base
        // tables through the call chain instead of the TVF being invisible.
        var tvf = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.tvfOrders", """
            CREATE FUNCTION dbo.tvfOrders(@cid INT)
            RETURNS TABLE
            AS
            RETURN (SELECT OrderId FROM dbo.Orders WHERE CustomerId = @cid);
            """);
        Assert.Null(tvf.Error);

        var caller = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT t.OrderId
                FROM dbo.Customers c
                CROSS APPLY dbo.tvfOrders(c.CustomerId) t;
            END
            """);
        Assert.Null(caller.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { tvf, caller }, includeColumns: true);

        // The caller CALLS the TVF (invoked as a table source via CROSS APPLY).
        Assert.NotNull(FindRel(graph, "CALLS",
            r => r.StartNodeId == $"{Db}::dbo.TestProc" && r.EndNodeId == $"{Db}::dbo.tvfOrders"));
        // The TVF's body reads the base table, so impact reaches Orders through the call.
        var orders = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Orders");
        Assert.NotNull(orders);
        Assert.NotNull(FindRel(graph, "READS_FROM",
            r => r.StartNodeId.StartsWith($"{Db}::dbo.tvfOrders") && r.EndNodeId == orders!.Id));
    }

    [Fact]
    public void OpenJson_ShreddedColumns_DeriveFromSourceJsonColumn()
    {
        // INSERT ... SELECT j.Field FROM T CROSS APPLY OPENJSON(T.Payload) WITH (...) j:
        // every shredded column is produced from the single source JSON column, so each
        // written column must DERIVE_FROM that column (and the column must be read).
        var graph = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.OrderLines (LineId, OrderId, Qty)
                SELECT j.LineId, j.OrderId, j.Qty
                FROM dbo.Orders o
                CROSS APPLY OPENJSON(o.Payload) WITH (LineId INT, OrderId INT, Qty INT) j;
            END
            """);

        var payload = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "Payload");
        Assert.NotNull(payload);
        // The JSON source column is read...
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.EndNodeId == payload!.Id));
        // ...and each written column derives from it.
        foreach (var col in new[] { "LineId", "OrderId", "Qty" })
        {
            var target = FindNode(graph, n => n.Labels.Contains("Column")
                && (string)n.Properties["table"] == "dbo.orderlines" && (string)n.Properties["name"] == col);
            Assert.NotNull(target);
            Assert.NotNull(FindRel(graph, "DERIVES_FROM",
                r => r.StartNodeId == target!.Id && r.EndNodeId == payload!.Id));
        }
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

        // The USING side is a real read, not just the target's write - confirms
        // MERGE's source tracking (CollectTableRefsInto on MergeSpecification.TableReference
        // in AstWalker.cs) isn't limited to the target.
        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.EndNodeId == source!.Id));
    }

    [Fact]
    public void Truncate_WritesToTarget()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                TRUNCATE TABLE dbo.Target;
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Target");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id && (string)r.Properties["action_type"] == "TRUNCATE"));
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
    public void DynamicSql_ResolvesQuotenameNcharCaseCoalesceToLiteral()
    {
        // Regression for extraction-gaps.md §5.1+§5.2: when every piece of the built
        // string is statically determinable, dynamic_sql must reconstruct the literal
        // SQL - exercising QUOTENAME(...), NCHAR(n) (via @CrLf), COALESCE(...) and a
        // CASE WHEN <comparison> THEN ... ELSE ... END, the exact shape WWI's
        // DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad uses.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                DECLARE @CrLf NVARCHAR(2) = NCHAR(13) + NCHAR(10)
                DECLARE @Schema SYSNAME = N'dbo'
                DECLARE @Table SYSNAME = N'Orders'
                DECLARE @Col SYSNAME = N'LastEditedBy'
                SET @sql = N'SELECT '
                    + CASE WHEN COALESCE(@Col, N'') <> N'' THEN QUOTENAME(@Col) ELSE N'NULL' END
                    + @CrLf + N'FROM ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) + N';'
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
        // dynamic_sql is whitespace-collapsed, so the NCHAR(13)+NCHAR(10) becomes a space.
        Assert.Equal("SELECT [LastEditedBy] FROM [dbo].[Orders];", (string)step.Properties["dynamic_sql"]);
    }

    [Fact]
    public void DynamicTrigger_EmitsTriggerNodeWithCreatesAndOnEdges()
    {
        // Fase A of docs/dynamic-trigger-modeling-spec.md: a CREATE TRIGGER built inside
        // dynamic SQL must surface as its own :Trigger node with a CREATES edge from the
        // creating proc and an ON edge to the table it fires on (decoupled - the trigger's
        // own body writes are NOT attributed to the proc). Mirrors WWI's
        // DeactivateTemporalTablesBeforeDataLoad building CREATE TRIGGER via EXEC(@sql).
        var sql = """
            CREATE PROCEDURE dbo.MakeTrigger
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                SET @sql = N'CREATE TRIGGER dbo.TR_Orders_Audit ON dbo.Orders AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END'
                EXEC (@sql)
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.MakeTrigger", sql);
        Assert.Null(result.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });

        var trigger = FindNode(graph, n => n.Labels.Contains("Trigger"));
        Assert.NotNull(trigger);
        Assert.Equal("dbo.TR_Orders_Audit", (string)trigger!.Properties["full_name"]);
        Assert.Equal("After", (string)trigger.Properties["trigger_timing"]);
        Assert.Equal(new[] { "INSERT", "UPDATE" }, (IReadOnlyList<string>)trigger.Properties["trigger_events"]);
        Assert.True((bool)trigger.Properties["is_dynamically_created"]);

        // proc -[:CREATES]-> trigger
        Assert.NotNull(FindRel(graph, "CREATES",
            r => r.StartNodeId == result.ObjectName && r.EndNodeId == trigger.Id));

        // trigger -[:ON]-> dbo.Orders
        var ordersTable = FindNode(graph, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Replace("[", "").Replace("]", "").ToLowerInvariant() == "dbo.orders");
        Assert.NotNull(ordersTable);
        Assert.NotNull(FindRel(graph, "ON",
            r => r.StartNodeId == trigger.Id && r.EndNodeId == ordersTable!.Id));
    }

    [Fact]
    public void Trigger_InsertedDeleted_ResolveToOnTable_NoPhantomTables()
    {
        // Regression for extraction-gaps.md §6: inside a DML trigger, the pseudo-tables
        // inserted/deleted are virtual row sets of the ON table - they must resolve to it
        // (reads and column lineage) and must never surface as their own :Table/:Column
        // nodes. Aliased (i/d) to also exercise alias-preserving resolution.
        var sql = """
            CREATE TRIGGER dbo.TR_Orders_Audit ON dbo.Orders
            AFTER UPDATE
            AS
            BEGIN
                INSERT INTO dbo.OrderAudit (OrderId, OldStatus, NewStatus)
                SELECT i.OrderId, d.Status, i.Status
                FROM inserted AS i
                INNER JOIN deleted AS d ON i.OrderId = d.OrderId;
            END
            """;
        var graph = BuildGraph(sql, objectName: "dbo.TR_Orders_Audit");

        // No phantom "inserted"/"deleted" nodes at all - neither :Table nor :Column.
        static string Plain(object name) => ((string)name).Replace("[", "").Replace("]", "").ToLowerInvariant();
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            (Plain(n.Properties["name"]) == "inserted" || Plain(n.Properties["name"]) == "deleted"));
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Column") &&
            (Plain(n.Properties["table"]) == "inserted" || Plain(n.Properties["table"]) == "deleted"));

        // The trigger reads its ON table (via inserted/deleted) and writes the audit table.
        var orders = FindNode(graph, n => n.Labels.Contains("Table") && Plain(n.Properties["name"]) == "dbo.orders");
        var audit = FindNode(graph, n => n.Labels.Contains("Table") && Plain(n.Properties["name"]) == "dbo.orderaudit");
        Assert.NotNull(orders);
        Assert.NotNull(audit);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.EndNodeId == orders!.Id));
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == audit!.Id));

        // Column lineage flows to the ON table's columns, not to any pseudo-table:
        // OldStatus/NewStatus both derive from dbo.Orders.Status (via deleted/inserted),
        // OrderId derives from dbo.Orders.OrderId.
        var statusOrders = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "Status");
        var idOrders = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "OrderId");
        var oldStatus = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orderaudit" && (string)n.Properties["name"] == "OldStatus");
        var newStatus = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orderaudit" && (string)n.Properties["name"] == "NewStatus");
        var idAudit = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orderaudit" && (string)n.Properties["name"] == "OrderId");
        Assert.NotNull(statusOrders);
        Assert.NotNull(idOrders);
        Assert.NotNull(oldStatus);
        Assert.NotNull(newStatus);
        Assert.NotNull(idAudit);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == oldStatus!.Id && r.EndNodeId == statusOrders!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == newStatus!.Id && r.EndNodeId == statusOrders!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == idAudit!.Id && r.EndNodeId == idOrders!.Id));
    }

    [Fact]
    public void DatabaseDdlTrigger_ParsesAndWritesGenerateEdges()
    {
        // Regression for the "DDL database triggers never extracted" gap: a
        // CREATE TRIGGER ... ON DATABASE (parent_class = 0 in sys.triggers) has no
        // schema_id and no row in sys.objects, so ObjectExtractor's original query
        // (JOIN sys.objects/sys.schemas) silently dropped it - and any table it wrote
        // to (e.g. AdventureWorks2019's dbo.DatabaseLog, fed by ddlDatabaseTriggerLog)
        // then showed up as a false "orphan table". Mirrors the real
        // ddlDatabaseTriggerLog trigger from AdventureWorks2019. ObjectExtractor now
        // files these under the synthetic pseudo-schema "$database" (see
        // ObjectExtractor.cs); this test exercises the parsing/lineage side using
        // that same "Database::$database.name" shape, independent of a live server.
        var sql = """
            CREATE TRIGGER [ddlDatabaseTriggerLog] ON DATABASE
            FOR DDL_DATABASE_LEVEL_EVENTS AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @data XML;
                DECLARE @eventType sysname;
                SET @data = EVENTDATA();
                SET @eventType = @data.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname');
                INSERT dbo.DatabaseLog
                    (PostTime, DatabaseUser, Event, [Schema], [Object], TSQL, XmlEvent)
                    VALUES
                    (GETDATE(), USER_NAME(), @eventType, '', '', '', @data);
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::$database.ddlDatabaseTriggerLog", sql);
        Assert.Null(result.Error);
        Assert.Equal("TRIGGER", result.ObjectType);

        var graph = GraphExporter.Build(new List<ObjectResult> { result });

        // The trigger's own SqlObject node exists and was filed under the
        // "$database" pseudo-schema, not misclassified into "dbo".
        var trigObject = FindNode(graph, n => n.Labels.Contains("SqlObject") &&
            (string)n.Properties["name"] == "ddlDatabaseTriggerLog");
        Assert.NotNull(trigObject);
        Assert.Equal("$database", (string)trigObject!.Properties["schema"]);

        // Its INSERT still surfaces as a real WRITES_TO edge onto dbo.DatabaseLog -
        // this is the edge that makes AdventureWorks2019's dbo.DatabaseLog stop
        // looking like an orphan table once the trigger is actually extracted.
        var databaseLog = FindNode(graph, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Replace("[", "").Replace("]", "").ToLowerInvariant() == "dbo.databaselog");
        Assert.NotNull(databaseLog);
        Assert.NotNull(FindRel(graph, "WRITES_TO",
            r => r.StartNodeId == $"{result.ObjectName}#step0" && r.EndNodeId == databaseLog!.Id));
    }

    [Fact]
    public void Update_FiltersOnWhereColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE dbo.Target SET Val = 1 WHERE Status = 'X'
            END
            """;
        var graph = BuildGraph(sql);

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "UPDATE");
        Assert.NotNull(step);

        var statusCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(statusCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == statusCol!.Id));
    }

    [Fact]
    public void SetScalarSubquery_BecomesStep_WithReadsFromAndFiltersOn()
    {
        // "SET @v = (SELECT ...)" used to be entirely invisible - no Step, so no
        // READS_FROM/FILTERS_ON for dbo.Source or its WHERE column.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @MinVal INT;
                SET @MinVal = (SELECT MIN(s.Val) FROM dbo.Source AS s WHERE s.Status = 'X');
            END
            """;
        var graph = BuildGraph(sql);

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "SELECT");
        Assert.NotNull(step);
        Assert.Equal("→ @MinVal", (string)step!.Properties["detail"]);

        var source = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Source");
        Assert.NotNull(source);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == step.Id && r.EndNodeId == source!.Id));

        var statusCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(statusCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step.Id && r.EndNodeId == statusCol!.Id));
    }

    [Fact]
    public void NestedExistsSubquery_ResolvesItsOwnTableAndFilterColumns()
    {
        // The EXISTS subquery's own table/columns ("inner") used to be silently
        // dropped (qualifier "i" didn't match the outer query's tableRefs).
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @MinVal INT;
                SET @MinVal = (SELECT MIN(o.Val) FROM dbo.Parent AS o
                               WHERE o.Status = 'X'
                               AND EXISTS (SELECT 1 FROM dbo.Child AS i WHERE i.ParentId = o.Id AND i.Flag = 1));
            END
            """;
        var graph = BuildGraph(sql);

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "SELECT");
        Assert.NotNull(step);

        var inner = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Child");
        Assert.NotNull(inner);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == step!.Id && r.EndNodeId == inner!.Id));

        var flagCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.child" && (string)n.Properties["name"] == "Flag");
        Assert.NotNull(flagCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == flagCol!.Id));

        // Outer table's own filter column must still resolve unchanged.
        var statusCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.parent" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(statusCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == statusCol!.Id));
    }

    [Fact]
    public void TopLevelSelect_WhereInSubquery_ResolvesSubqueryTableFilterColumns()
    {
        // Same nested-subquery fix, but for a top-level SELECT's WHERE ... IN (subquery) -
        // confirms the fix isn't specific to SET assignments.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT * FROM dbo.Orders AS o
                WHERE o.CustomerId IN (SELECT c.Id FROM dbo.Customers AS c WHERE c.Active = 1);
            END
            """;
        var graph = BuildGraph(sql);

        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "SELECT");
        Assert.NotNull(step);

        var customers = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Customers");
        Assert.NotNull(customers);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == step!.Id && r.EndNodeId == customers!.Id));

        var activeCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.customers" && (string)n.Properties["name"] == "Active");
        Assert.NotNull(activeCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == activeCol!.Id));
    }

    [Fact]
    public void DynamicSql_LiteralWhereClause_ResolvesToFiltersOnColumn()
    {
        // The executed string is a pure literal (no @variables), so
        // SqlAnalyzer.ResolveDynamicSqlLinks re-parses it and the inner UPDATE's
        // own WHERE clause should surface as FILTERS_ON, same as a direct UPDATE.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                EXEC ('UPDATE dbo.Target SET Val = 1 WHERE Status = ''X''')
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        Assert.Equal(1, result.DynamicSqlCount);

        var resolvedUpdate = result.FlowLinks.FirstOrDefault(fl => fl.ConsequenceType == "UPDATE");
        Assert.NotNull(resolvedUpdate);
        Assert.Contains(resolvedUpdate!.FilterColumns, tc => tc.Table == "dbo.Target" && tc.Columns.Contains("Status"));

        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
        var step = FindNode(graph, n => n.Labels.Contains("Step") && (string)n.Properties["action"] == "UPDATE");
        Assert.NotNull(step);

        var statusCol = FindNode(graph, n => n.Labels.Contains("Column") &&
            (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(statusCol);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == statusCol!.Id));
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
    public void ExecCall_CrossDatabase_ResolvesToCalleeAndTagsCrossDb()
    {
        const string otherDb = "OtherDb";
        var callerSql = """
            CREATE PROCEDURE dbo.A
            AS
            BEGIN
                EXEC OtherDb.dbo.B
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
        var resultB = SqlAnalyzer.AnalyzeObject($"{otherDb}::dbo.B", calleeSql);
        Assert.Null(resultA.Error);
        Assert.Null(resultB.Error);

        var graph = GraphExporter.Build(new List<ObjectResult> { resultA, resultB });

        var rel = FindRel(graph, "CALLS",
            r => r.StartNodeId == $"{Db}::dbo.A" && r.EndNodeId == $"{otherDb}::dbo.B");
        Assert.NotNull(rel);
        Assert.True((bool)rel!.Properties["is_cross_database"]);
        Assert.Equal(Db, rel.Properties["source_database"]);
        Assert.Equal(otherDb, rel.Properties["target_database"]);
    }

    [Fact]
    public void InsertSelect_ThroughTempTable_BridgesDerivesFromToRealSourceTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                CREATE TABLE #Temp (Col INT);
                INSERT INTO #Temp (Col) SELECT Col FROM dbo.Fisico1;
                INSERT INTO dbo.Fisico2 (Col) SELECT Col FROM #Temp;
            END
            """;
        var graph = BuildGraph(sql);

        // No phantom Table node for the #temp bridge.
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("Temp", StringComparison.OrdinalIgnoreCase));

        var rel = FindRel(graph, "DERIVES_FROM", r =>
            r.StartNodeId.EndsWith(":table:dbo.fisico2:column:Col", StringComparison.OrdinalIgnoreCase) &&
            r.EndNodeId.EndsWith(":table:dbo.fisico1:column:Col", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rel);
        Assert.Equal("#Temp", rel!.Properties["via_transient"]);
    }

    [Fact]
    public void DynamicInsert_ThroughTempTable_BridgesToRealSourceLikeStatic()
    {
        // The #staging write lives inside a reconstructed EXEC(@sql) literal; the read
        // of #staging into a real target is static. The dynamic write must feed the same
        // tempOrigin bridge the static case uses, so the end result is identical to
        // InsertSelect_ThroughTempTable_BridgesDerivesFromToRealSourceTable: a
        // DERIVES_FROM RealTarget.* -> RealSource.* via #staging, and NO #staging node.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'INSERT INTO #staging (Id, Name) SELECT Id, Name FROM dbo.RealSource';
                EXEC(@sql);
                INSERT INTO dbo.RealTarget (Id, Name) SELECT Id, Name FROM #staging;
            END
            """;
        var graph = BuildGraph(sql);

        // No phantom Table node for the transient bridge.
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("staging", StringComparison.OrdinalIgnoreCase));

        var idBridge = FindRel(graph, "DERIVES_FROM", r =>
            r.StartNodeId.EndsWith(":table:dbo.realtarget:column:Id", StringComparison.OrdinalIgnoreCase) &&
            r.EndNodeId.EndsWith(":table:dbo.realsource:column:Id", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(idBridge);
        Assert.Equal("#staging", idBridge!.Properties["via_transient"]);

        var nameBridge = FindRel(graph, "DERIVES_FROM", r =>
            r.StartNodeId.EndsWith(":table:dbo.realtarget:column:Name", StringComparison.OrdinalIgnoreCase) &&
            r.EndNodeId.EndsWith(":table:dbo.realsource:column:Name", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nameBridge);
    }

    [Fact]
    public void DynamicInsert_WrappedInIf_ThroughTempTable_StillBridges()
    {
        // FRK's dominant dynamic shape: the executed literal wraps its DML in control
        // flow ("IF <guard> INSERT INTO #staging ..."). ResolveDynamicSqlLinks must
        // descend the IF to reach the nested INSERT so the temp bridge still forms;
        // otherwise the whole statement (and its lineage) is silently dropped.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'IF 1 = 1 INSERT INTO #staging (Id, Name) SELECT Id, Name FROM dbo.RealSource';
                EXEC(@sql);
                INSERT INTO dbo.RealTarget (Id, Name) SELECT Id, Name FROM #staging;
            END
            """;
        var graph = BuildGraph(sql);

        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("staging", StringComparison.OrdinalIgnoreCase));

        var bridge = FindRel(graph, "DERIVES_FROM", r =>
            r.StartNodeId.EndsWith(":table:dbo.realtarget:column:Id", StringComparison.OrdinalIgnoreCase) &&
            r.EndNodeId.EndsWith(":table:dbo.realsource:column:Id", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bridge);
        Assert.Equal("#staging", bridge!.Properties["via_transient"]);
    }

    [Fact]
    public void DynamicInsert_WrappedInIf_WriteToRealTable_IsCaptured()
    {
        // Same control-flow shape but the INSERT targets a REAL table (FRK step13/287:
        // "IF EXISTS(...) INSERT [DBAtools].[dbo].[BlitzFirst] ..."). Before the fix the
        // whole reconstructed statement was dropped and this write vanished; it must now
        // surface as a real WRITES_TO.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'IF EXISTS (SELECT * FROM dbo.Config) INSERT INTO dbo.RealTarget (Id) SELECT Id FROM dbo.RealSource';
                EXEC(@sql);
            END
            """;
        var graph = BuildGraph(sql);

        var target = FindNode(graph, n => n.Labels.Contains("Table") &&
            (string)n.Properties["name"] == "dbo.RealTarget");
        Assert.NotNull(target);
        Assert.NotNull(FindRel(graph, "WRITES_TO", r => r.EndNodeId == target!.Id));
        // The nested SELECT source is real lineage too.
        Assert.NotNull(FindRel(graph, "READS_FROM",
            r => r.EndNodeId.EndsWith(":table:dbo.realsource", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DynamicInsert_RuntimeVariableSource_InventsNoLineage()
    {
        // The executed string is assembled from a runtime value (@tableName), so it can
        // never be reconstructed to a literal: DynamicSqlText stays empty, no inner DML
        // is parsed, and NO bridge/read/write may be invented. Fails closed - the whole
        // point of only ever bridging reconstructed literals.
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @tableName SYSNAME
            AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'INSERT INTO #staging (Id) SELECT Id FROM ' + QUOTENAME(@tableName);
                EXEC(@sql);
                INSERT INTO dbo.RealTarget (Id) SELECT Id FROM #staging;
            END
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);

        // Nothing was reconstructed, so the EXEC step carries no resolved DML text.
        var exec = result.FlowLinks.FirstOrDefault(fl => fl.ConsequenceType == "EXEC");
        Assert.NotNull(exec);
        Assert.Equal("", exec!.DynamicSqlText);

        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        // No bridge may be invented: #staging was never populated by a known source.
        Assert.DoesNotContain(graph.Relationships, r => r.Type == "DERIVES_FROM" &&
            r.EndNodeId.EndsWith(":table:dbo.realsource:column:Id", StringComparison.OrdinalIgnoreCase));
        // And still no phantom temp node.
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains("staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TempTablePolicy_NoTempTableNodeFromAnyPath()
    {
        // Exercises every path that used to leak a phantom #temp :Table node:
        //   1. bracketed temp target [#Staging]  -> the [#BlitzResults] main-branch leak
        //   2. SELECT ... INTO ##Global          -> global-temp write target
        //   3. SELECT @var = Col FROM #Staging    -> the ASSIGNED_FROM leak (11 FRK ghosts)
        // ...while confirming the *real* tables joined in (ExtraReads) are still graphed.
        var sql = """
            CREATE PROCEDURE dbo.TempPolicyProc
            AS
            BEGIN
                CREATE TABLE #Staging (Id INT, Name NVARCHAR(50));

                INSERT INTO [#Staging] (Id, Name)
                SELECT s.Id, s.Name FROM dbo.RealSource AS s
                JOIN dbo.RealPartner AS p ON s.Id = p.Id;

                SELECT g.Id, g.Name INTO ##GlobalDump
                FROM #Staging AS g JOIN dbo.RealPartner2 AS q ON g.Id = q.Id;

                DECLARE @Cnt INT;
                SELECT @Cnt = Id FROM #Staging WHERE Name = N'x';
            END
            """;
        var graph = BuildGraph(sql);

        // No :Table node may carry a temp name, in any (bracketed / global) spelling.
        static bool IsTempName(string n) => n.TrimStart('[', ' ').StartsWith('#');
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            IsTempName((string)n.Properties["name"]));

        // The real JOIN partners read alongside the temp writes must still surface -
        // the guard removes phantom temps, it must not swallow real reads.
        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Table") &&
            (string)n.Properties["name"] == "dbo.RealPartner"));
        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Table") &&
            (string)n.Properties["name"] == "dbo.RealPartner2"));
        Assert.NotNull(FindRel(graph, "READS_FROM",
            r => r.EndNodeId.EndsWith(":table:dbo.realpartner", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BracketedTableTarget_DisplayNameIsDebracketed()
    {
        // H4 (frk-torture-report.md): 9/138 :Table nodes in the FRK corpus kept
        // T-SQL brackets in their display `name` (e.g. "[msdb].[dbo].[sysjobs]")
        // while the rest were bracket-free ("msdb.dbo.sysjobs"). The node `id` was
        // already normalized via NormalizeRef - only the display name leaked brackets.
        var sql = """
            CREATE PROCEDURE dbo.P
            AS
            INSERT INTO [msdb].[dbo].[sysjobs] (name) VALUES ('x')
            """;
        var graph = BuildGraph(sql);

        Assert.NotNull(FindNode(graph, n => n.Labels.Contains("Table") &&
            (string)n.Properties["name"] == "msdb.dbo.sysjobs"));
        Assert.DoesNotContain(graph.Nodes, n => n.Labels.Contains("Table") &&
            ((string)n.Properties["name"]).Contains('['));
    }

    [Fact]
    public void SelectFromAnalyzedView_BridgesToViewsRealBaseTable()
    {
        var viewSql = """
            CREATE VIEW dbo.VCustomers
            AS
            SELECT CustomerID, CustomerName FROM dbo.Customers
            """;
        var consumerSql = """
            CREATE PROCEDURE dbo.Consumer
            AS
            BEGIN
                SELECT CustomerID, CustomerName FROM dbo.VCustomers
            END
            """;

        var view = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.VCustomers", viewSql);
        var consumer = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.Consumer", consumerSql);
        Assert.Null(view.Error);
        Assert.Null(consumer.Error);
        Assert.Equal("VIEW", view.ObjectType);

        var graph = GraphExporter.Build(new List<ObjectResult> { view, consumer }, includeColumns: true);

        // Still resolves the VIEW itself (Step -> SqlObject).
        Assert.NotNull(FindRel(graph, "TARGETS",
            r => r.StartNodeId == $"{Db}::dbo.Consumer#step0" && r.EndNodeId == $"{Db}::dbo.VCustomers"));

        // ...and bridges straight through to the view's real base table/columns.
        var readsFrom = FindRel(graph, "READS_FROM",
            r => r.StartNodeId == $"{Db}::dbo.Consumer#step0" &&
                 r.EndNodeId == $"{Db}:table:dbo.customers");
        Assert.NotNull(readsFrom);
        Assert.Equal($"{Db}::dbo.VCustomers", readsFrom!.Properties["via_view"]);

        Assert.NotNull(FindRel(graph, "READS_COLUMN",
            r => r.StartNodeId == $"{Db}::dbo.Consumer#step0" &&
                 r.EndNodeId == $"{Db}:table:dbo.customers:column:CustomerName"));
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

    [Fact]
    public void ComputedColumn_DerivesFromItsSourceColumns()
    {
        var sql = """
            CREATE TABLE dbo.OrderLines
            (
                Id INT NOT NULL IDENTITY(1,1) PRIMARY KEY,
                Price DECIMAL(10,2) NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty) PERSISTED
            )
            """;
        var table = TableAnalyzer.AnalyzeTable($"{Db}::dbo.OrderLines", sql);
        Assert.Null(table.Error);

        var graph = GraphExporter.Build(new List<ObjectResult>(), includeColumns: true,
            tableSchemas: new List<TableSchemaResult> { table });

        var totalCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orderlines" && (string)n.Properties["name"] == "Total");
        var priceCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orderlines" && (string)n.Properties["name"] == "Price");
        var qtyCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orderlines" && (string)n.Properties["name"] == "Qty");
        Assert.NotNull(totalCol);
        Assert.NotNull(priceCol);
        Assert.NotNull(qtyCol);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == priceCol!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == qtyCol!.Id));
    }

    // op_kinds: the structured operator tokens carried alongside the raw `logic` text
    // on lineage/filter edges (see OperatorClassifier), so a rule engine can reason
    // about the *kind* of dependency (arithmetic vs comparison vs concat) not just text.
    private static IReadOnlyList<string> Ops(GraphRel? r) =>
        r?.Properties.TryGetValue("op_kinds", out var v) == true ? ((IEnumerable<string>)v).ToList() : new List<string>();

    [Fact]
    public void ComputedColumn_CarriesArithmeticOpKind()
    {
        var sql = """
            CREATE TABLE dbo.OrderLines
            (
                Id INT NOT NULL IDENTITY(1,1) PRIMARY KEY,
                Price DECIMAL(10,2) NOT NULL,
                Qty INT NOT NULL,
                Total AS (Price * Qty) PERSISTED
            )
            """;
        var table = TableAnalyzer.AnalyzeTable($"{Db}::dbo.OrderLines", sql);
        Assert.Null(table.Error);
        var graph = GraphExporter.Build(new List<ObjectResult>(), includeColumns: true,
            tableSchemas: new List<TableSchemaResult> { table });

        var totalCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.orderlines" && (string)n.Properties["name"] == "Total");
        var rel = FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id);
        Assert.Contains("arith:*", Ops(rel));
    }

    [Fact]
    public void InsertSelectExpression_CarriesArithmeticOpKind()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO dbo.Target (Total)
                SELECT s.Price + s.Tax FROM dbo.Source s
            END
            """;
        var graph = BuildGraph(sql);
        var totalCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Total");
        var rel = FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id);
        Assert.Contains("arith:+", Ops(rel));
    }

    [Fact]
    public void UpdateSetExpression_DerivesTargetColumnFromSourceColumns()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE dbo.Target SET Total = Price * Qty WHERE Id > 0
            END
            """;
        var graph = BuildGraph(sql);
        var totalCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Total");
        var priceCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Price");
        var qtyCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Qty");
        Assert.NotNull(totalCol);
        Assert.NotNull(priceCol);
        Assert.NotNull(qtyCol);

        var toPrice = FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == priceCol!.Id);
        Assert.NotNull(toPrice);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == qtyCol!.Id));
        Assert.Contains("arith:*", Ops(toPrice));
    }

    [Fact]
    public void UpdateSetFromJoin_DerivesTargetColumnFromOtherTable()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE t SET t.Total = s.Amount
                FROM dbo.Target t JOIN dbo.Source s ON s.Id = t.Id
            END
            """;
        var graph = BuildGraph(sql);
        var totalCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Total");
        var amountCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.source" && (string)n.Properties["name"] == "Amount");
        Assert.NotNull(totalCol);
        Assert.NotNull(amountCol);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == amountCol!.Id));
    }

    [Fact]
    public void UpdateSetSelfReference_DoesNotCreateSelfLoop()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                UPDATE dbo.Target SET Counter = Counter + 1 WHERE Id > 0
            END
            """;
        var graph = BuildGraph(sql);
        var counterCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Counter");
        Assert.NotNull(counterCol);
        Assert.Null(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == counterCol!.Id && r.EndNodeId == counterCol!.Id));
    }

    [Fact]
    public void View_OutputColumnDerivesFromBaseColumns()
    {
        var sql = """
            CREATE VIEW dbo.OrderSummary AS
            SELECT o.Id AS OrderId, o.Price * o.Qty AS Total
            FROM dbo.Orders o
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.OrderSummary", sql);
        Assert.Null(result.Error);
        Assert.Equal("VIEW", result.ObjectType);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        var totalCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.ordersummary" && (string)n.Properties["name"] == "Total");
        var priceCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "Price");
        var qtyCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "Qty");
        var orderIdCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.ordersummary" && (string)n.Properties["name"] == "OrderId");
        var idCol = FindNode(graph, n => n.Labels.Contains("Column") && (string)n.Properties["table"] == "dbo.orders" && (string)n.Properties["name"] == "Id");
        Assert.NotNull(totalCol);
        Assert.NotNull(priceCol);
        Assert.NotNull(qtyCol);
        Assert.NotNull(orderIdCol);

        var toPrice = FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == priceCol!.Id);
        Assert.NotNull(toPrice);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == totalCol!.Id && r.EndNodeId == qtyCol!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == orderIdCol!.Id && r.EndNodeId == idCol!.Id));
        Assert.Contains("arith:*", Ops(toPrice));
        Assert.True((bool)toPrice!.Properties["via_view"]);
    }

    [Fact]
    public void WherePredicate_FiltersOnCarriesLogicalAndComparisonOpKinds()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT Id FROM dbo.Source WHERE Status = 'X' AND Qty > 0
            END
            """;
        var graph = BuildGraph(sql);
        var rel = FindRel(graph, "FILTERS_ON");
        Assert.NotNull(rel);
        var ops = Ops(rel);
        Assert.Contains("logical:AND", ops);
        Assert.Contains("compare:=", ops);
        Assert.Contains("compare:>", ops);
    }

    [Fact]
    public void VariableConcatenation_CarriesConcatOpKind()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
                @Name NVARCHAR(100)
            AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX)
                SET @SQL = 'CREATE INDEX IX ON dbo.T (' + @Name + ')'
            END
            """;
        var graph = BuildGraph(sql);
        var varNode = FindNode(graph, n => n.Labels.Contains("Variable") && (string)n.Properties["name"] == "@SQL");
        Assert.NotNull(varNode);
        Assert.Contains("arith:+", ((IEnumerable<string>)varNode!.Properties["op_kinds"]).ToList());
    }

    [Fact]
    public void DropColumn_LinksAlterStepToAffectedColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                ALTER TABLE dbo.Target DROP COLUMN LegacyFlag
            END
            """;
        var graph = BuildGraph(sql);

        var legacyCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "LegacyFlag");
        Assert.NotNull(legacyCol);

        var writesColumn = FindRel(graph, "WRITES_COLUMN", r => r.EndNodeId == legacyCol!.Id);
        Assert.NotNull(writesColumn);
        Assert.Equal("DROP COLUMN", (string)writesColumn!.Properties["detail"]);
    }

    [Fact]
    public void AlterColumn_LinksAlterStepToAffectedColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                ALTER TABLE dbo.Target ALTER COLUMN Status VARCHAR(50) NOT NULL
            END
            """;
        var graph = BuildGraph(sql);

        var statusCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.target" && (string)n.Properties["name"] == "Status");
        Assert.NotNull(statusCol);

        var writesColumn = FindRel(graph, "WRITES_COLUMN", r => r.EndNodeId == statusCol!.Id);
        Assert.NotNull(writesColumn);
        Assert.Equal("ALTER COLUMN", (string)writesColumn!.Properties["detail"]);
    }

    // ---------------------------------------------------------------------
    // Test-debt closed (coverage-matrix.md, eje A1.1): four behaviours that
    // were validated only by the eval/community-edge-cases corpus now have a
    // unit-level regression net, so an eje-B refactor can't silently break them.
    // ---------------------------------------------------------------------

    // Mirrors eval/community-edge-cases/dml-advanced/merge-with-output.sql.
    // OUTPUT ... INTO writes the inserted/deleted pseudo-columns of the MERGE
    // target into a log table; the log columns must derive from the target's
    // real column, not vanish (ScriptDOM exposes this on OutputIntoClause).
    [Fact]
    public void MergeOutputInto_LogColumnsDeriveFromTargetColumn()
    {
        var sql = """
            CREATE PROCEDURE dbo.usp_SyncProducts AS
            BEGIN
              MERGE dbo.TargetProducts AS t
              USING dbo.SourceProducts AS s ON t.Id = s.Id
              WHEN MATCHED THEN UPDATE SET t.Price = s.Price
              WHEN NOT MATCHED THEN INSERT (Id, Price) VALUES (s.Id, s.Price)
              OUTPUT deleted.Price AS OldPrice, inserted.Price AS NewPrice
              INTO dbo.ProductMergeLog (OldPrice, NewPrice);
            END
            """;
        var graph = BuildGraph(sql);

        var targetPrice = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.targetproducts" && (string)n.Properties["name"] == "Price");
        var newPrice = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.productmergelog" && (string)n.Properties["name"] == "NewPrice");
        var oldPrice = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.productmergelog" && (string)n.Properties["name"] == "OldPrice");
        Assert.NotNull(targetPrice);
        Assert.NotNull(newPrice);
        Assert.NotNull(oldPrice);

        // inserted.Price / deleted.Price both resolve to the target's Price column.
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == newPrice!.Id && r.EndNodeId == targetPrice!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == oldPrice!.Id && r.EndNodeId == targetPrice!.Id));
    }

    // Mirrors eval/community-edge-cases/set-ops/union-view.sql. A view whose body
    // is a UNION (BinaryQueryExpression) must derive its output column from the
    // positionally-matching column of EVERY branch, not just the first.
    [Fact]
    public void ViewWithUnionBody_OutputColumnDerivesFromAllBranches()
    {
        var sql = """
            CREATE VIEW dbo.vUnion AS
            SELECT a FROM dbo.t1
            UNION
            SELECT b FROM dbo.t2
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.vUnion", sql);
        Assert.Null(result.Error);
        Assert.Equal("VIEW", result.ObjectType);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        var outCol = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.vunion" && (string)n.Properties["name"] == "a");
        var t1a = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.t1" && (string)n.Properties["name"] == "a");
        var t2b = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.t2" && (string)n.Properties["name"] == "b");
        Assert.NotNull(outCol);
        Assert.NotNull(t1a);
        Assert.NotNull(t2b);

        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == outCol!.Id && r.EndNodeId == t1a!.Id));
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == outCol!.Id && r.EndNodeId == t2b!.Id));
    }

    // Mirrors eval/community-edge-cases/cte-recursive/recursive-cte.sql. The view's
    // body is a recursive CTE (anchor UNION ALL recursive member); lineage of the
    // real columns must reach the base table through both members.
    [Fact]
    public void ViewWithRecursiveCte_TracesRealColumnsToBaseTable()
    {
        var sql = """
            CREATE VIEW dbo.vOrgChart AS
            WITH cte AS (
              SELECT EmployeeID, ManagerID, 0 AS Lvl
              FROM dbo.Employees WHERE ManagerID IS NULL
              UNION ALL
              SELECT e.EmployeeID, e.ManagerID, c.Lvl + 1
              FROM dbo.Employees e JOIN cte c ON e.ManagerID = c.EmployeeID
            )
            SELECT EmployeeID, ManagerID, Lvl FROM cte
            """;
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.vOrgChart", sql);
        Assert.Null(result.Error);
        Assert.Equal("VIEW", result.ObjectType);
        var graph = GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);

        var employees = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Employees");
        Assert.NotNull(employees);
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.EndNodeId == employees!.Id));

        var outEmpId = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.vorgchart" && (string)n.Properties["name"] == "EmployeeID");
        var baseEmpId = FindNode(graph, n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == "dbo.employees" && (string)n.Properties["name"] == "EmployeeID");
        Assert.NotNull(outEmpId);
        Assert.NotNull(baseEmpId);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == outEmpId!.Id && r.EndNodeId == baseEmpId!.Id));
    }

    // Declarative DDL constraints (CHECK/DEFAULT/UNIQUE) surface as :BusinessRule
    // nodes — HAS_RULE from the table, CONSTRAINS to each governed column. PK/FK/
    // NOT NULL deliberately stay as column attributes / FK edges, not rules.
    [Fact]
    public void Constraints_CheckDefaultUnique_ProduceBusinessRuleNodes()
    {
        var tableSql = """
            CREATE TABLE dbo.Products
            (
                Id INT NOT NULL IDENTITY(1,1) PRIMARY KEY,
                Price DECIMAL(10,2) NOT NULL CONSTRAINT CK_Products_Price CHECK (Price > 0),
                Qty INT NOT NULL CONSTRAINT DF_Products_Qty DEFAULT (0),
                Sku NVARCHAR(50) NOT NULL CONSTRAINT UQ_Products_Sku UNIQUE
            )
            """;
        var products = TableAnalyzer.AnalyzeTable($"{Db}::dbo.Products", tableSql);
        Assert.Null(products.Error);

        var graph = GraphExporter.Build(new List<ObjectResult>(), includeColumns: true,
            tableSchemas: new List<TableSchemaResult> { products });

        var table = FindNode(graph, n => n.Labels.Contains("Table") && (string)n.Properties["name"] == "dbo.Products");
        Assert.NotNull(table);

        // Exactly three rules: PK and NOT NULL must NOT appear here.
        var rules = graph.Nodes.Where(n => n.Labels.Contains("BusinessRule")).ToList();
        Assert.Equal(3, rules.Count);

        void AssertRule(string kind, string columnName)
        {
            var rule = rules.FirstOrDefault(n => (string)n.Properties["kind"] == kind);
            Assert.NotNull(rule);
            // The table owns the rule...
            Assert.NotNull(FindRel(graph, "HAS_RULE", r => r.StartNodeId == table!.Id && r.EndNodeId == rule!.Id));
            // ...and it constrains the expected column.
            var col = FindNode(graph, n => n.Labels.Contains("Column")
                && (string)n.Properties["table"] == "dbo.products" && (string)n.Properties["name"] == columnName);
            Assert.NotNull(col);
            Assert.NotNull(FindRel(graph, "CONSTRAINS", r => r.StartNodeId == rule!.Id && r.EndNodeId == col!.Id));
        }

        AssertRule("CHECK", "Price");
        AssertRule("DEFAULT", "Qty");
        AssertRule("UNIQUE", "Sku");
    }
}
