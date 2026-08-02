using TSqlParser;
using Xunit;

namespace TSqlParser.Tests;

/// <summary>
/// Regression coverage for the temp/table-variable filter added to
/// ExecutionPlanParser.CollectTableAccesses. Before this fix, enrich-from-plans
/// treated #temp tables and @table variables surfaced by a plan's Object
/// elements as real discoveries - 64% of the FRK's 906 "discovered" edges
/// (547 temp + 34 table-variable) were exactly this noise, while the rest of
/// the engine already excludes them via GraphExporter.IsTempOrVariable.
/// </summary>
public class ExecutionPlanParserTempFilterTests
{
    private static string PlanTouching(params (string schema, string table)[] objects)
    {
        var relOps = string.Join("", objects.Select(o =>
            $"""<RelOp PhysicalOp="Table Insert"><Object Database="[TestDb]" Schema="[{o.schema}]" Table="[{o.table}]"/></RelOp>"""));
        return $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementType="INSERT"><QueryPlan>{relOps}</QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
            """;
    }

    [Fact]
    public void LocalTempTable_IsExcludedFromTableAccesses()
    {
        var plan = ExecutionPlanParser.ParseXml(PlanTouching(("dbo", "#CurrentValue")));
        var stmt = Assert.Single(Assert.Single(plan.Procedures).Statements);
        Assert.Empty(stmt.TableAccesses);
    }

    [Fact]
    public void GlobalTempTable_IsExcludedFromTableAccesses()
    {
        var plan = ExecutionPlanParser.ParseXml(PlanTouching(("dbo", "##GlobalStaging")));
        var stmt = Assert.Single(Assert.Single(plan.Procedures).Statements);
        Assert.Empty(stmt.TableAccesses);
    }

    [Fact]
    public void TableVariable_IsExcludedFromTableAccesses()
    {
        // SQL Server plans can surface a table variable's spool as an Object
        // element too, schema-less (e.g. Table="[@Ids]").
        var plan = ExecutionPlanParser.ParseXml(PlanTouching(("", "@Ids")));
        var stmt = Assert.Single(Assert.Single(plan.Procedures).Statements);
        Assert.Empty(stmt.TableAccesses);
    }

    [Fact]
    public void RealBusinessTable_IsStillCaptured_AlongsideExcludedTemp()
    {
        // The filter must not over-fire and drop real tables in the same
        // statement as a temp/variable reference.
        var plan = ExecutionPlanParser.ParseXml(PlanTouching(("Dimension", "City"), ("dbo", "#CurrentValue")));
        var stmt = Assert.Single(Assert.Single(plan.Procedures).Statements);
        var access = Assert.Single(stmt.TableAccesses);
        Assert.Equal("Dimension.City", access.FullName.Replace("TestDb.", ""));
    }
}
