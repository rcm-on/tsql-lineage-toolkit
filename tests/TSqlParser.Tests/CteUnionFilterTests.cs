using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// A WHERE inside a CTE body or inside one branch of a top-level UNION used to be
/// dropped entirely: the CTE was resolved to its base tables and the set operation
/// only had its FROM walked, so neither branch's own WhereClause ever reached
/// ExtractFilterColumns. For a recursive CTE that meant losing the stop condition -
/// the one predicate that is pure business logic. These lock that in.
/// </summary>
public class CteUnionFilterTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql)
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    /// <summary>True when some FILTERS_ON edge lands on "<table>.<column>".</summary>
    private static bool FiltersOn(GraphPayload g, string table, string column) =>
        g.Relationships.Any(r => r.Type == "FILTERS_ON" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId &&
                n.Labels.Contains("Column") &&
                string.Equals((string)n.Properties["table"], table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)n.Properties["name"], column, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void CteBody_WhereIsCaptured()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                WITH c AS (SELECT Id FROM dbo.T4 WHERE T4.Borrado = 0)
                SELECT Id FROM c;
            END
            """);
        Assert.True(FiltersOn(g, "dbo.t4", "Borrado"), "el WHERE del cuerpo de la CTE debe producir FILTERS_ON");
    }

    [Fact]
    public void RecursiveCte_AnchorAndStopCondition_AreCaptured_AndAnalysisTerminates()
    {
        // If the CTE-body walk did not guard against the self-reference, this hangs.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                WITH r AS (
                    SELECT Id, 0 AS Nivel FROM dbo.T5 WHERE T5.Raiz = 1
                    UNION ALL
                    SELECT t.Id, r.Nivel + 1 FROM dbo.T5 t JOIN r ON t.Padre = r.Id WHERE r.Nivel < 5)
                SELECT Id FROM r;
            END
            """);
        Assert.True(FiltersOn(g, "dbo.t5", "Raiz"), "el WHERE del ancla debe producir FILTERS_ON");
        // The stop condition references the CTE itself; it is attributed to the CTE's
        // base table, which is the only real relation behind "r".
        Assert.True(FiltersOn(g, "dbo.t5", "Nivel"), "la condicion de parada de la recursion debe producir FILTERS_ON");
    }

    [Fact]
    public void TopLevelUnion_BothBranchWheresAreCaptured()
    {
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT Id FROM dbo.A WHERE A.Flag = 1
                UNION ALL
                SELECT Id FROM dbo.B WHERE B.Estado = 'X';
            END
            """);
        Assert.True(FiltersOn(g, "dbo.a", "Flag"), "el WHERE de la primera rama del UNION se pierde");
        Assert.True(FiltersOn(g, "dbo.b", "Estado"), "el WHERE de la segunda rama del UNION se pierde");
    }

    [Fact]
    public void SubqueryWhere_StillAttributedToInnerTable()
    {
        // Control: this already worked; the CTE/UNION fix must not re-route it to dbo.T2.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                SELECT Id FROM dbo.T2 WHERE Id IN (SELECT Id FROM dbo.T3 WHERE T3.Estado = 'X');
            END
            """);
        Assert.True(FiltersOn(g, "dbo.t3", "Estado"), "el WHERE de la subconsulta debe seguir apuntando a la tabla de dentro");
        Assert.False(FiltersOn(g, "dbo.t2", "Estado"), "no debe atribuirse a la tabla de fuera");
    }

    [Fact]
    public void Governs_IsUnchangedByFilterExtraction()
    {
        // Control: filters constrain a step, they never branch the flow. The flowchart
        // is built on GOVERNS, so it must not gain edges from a WHERE.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                IF @@ROWCOUNT > 0
                BEGIN
                    WITH c AS (SELECT Id FROM dbo.T4 WHERE T4.Borrado = 0)
                    SELECT Id FROM c;
                END
            END
            """);
        var governs = g.Relationships.Count(r => r.Type == "GOVERNS");
        // The IF governs both the CTE-filter step and the SELECT that consumes it.
        Assert.Equal(2, governs);
    }
}
