using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Columns referenced ONLY inside a subquery nested in a predicate/expression
/// (EXISTS/NOT EXISTS, IN (SELECT ...), scalar comparison "= (SELECT ...)", and a
/// derived table in FROM) used to be silently dropped whenever the column's name
/// collided, unqualified, with a column of the same name on the outer table -
/// extremely common for FK id columns (e.g. "ModuleDefID" existing on both an outer
/// permissions table and the table it's filtered against). Root cause: AstWalker's
/// ExtractFilterColumnsCore flattened the outer predicate's own column refs together
/// with every nested subquery's refs into ONE list, then resolved all of them against
/// the UNION of outer+nested FROM tables - so an unqualified name present in both
/// scopes became "ambiguous" (ResolveUnqualified's deliberately-conservative rule) and
/// got dropped from BOTH scopes, even though each occurrence is perfectly resolvable
/// within its own scope. Measured as causa #1 in eval/column-recall/blind-refs.md: 58
/// of 140 blind column refs on the DNN Platform corpus (41%).
///
/// Fixtures live in eval/community-edge-cases/subquery-predicates/*.sql (loaded via the
/// real SqlFileLoader -> InputAnalyzer -> GraphExporter pipeline, same as
/// CommunityEdgeCaseGateTests, so CREATE TABLE definitions provide the real schema
/// ambiguity that triggered the bug on the DNN corpus).
/// </summary>
public class SubqueryColumnLineageTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "subquery-column-lineage-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Sube desde el bin de tests hasta la raíz del toolkit (la carpeta que contiene eval/community-edge-cases).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "community-edge-cases", "run.mjs")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontró eval/community-edge-cases/run.mjs subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }

    private GraphPayload BuildGraph(string relativeSqlPath)
    {
        var root = RepoRoot();
        var sqlPath = Path.Combine(root, "eval", "community-edge-cases", "subquery-predicates", relativeSqlPath);
        Assert.True(File.Exists(sqlPath), $"Fixture no encontrada: {sqlPath}");

        var inputPath = Path.Combine(NewTempDir(), "input.json");
        Assert.Equal(0, SqlFileLoader.Run("SubqueryPredicatesDB", inputPath, new[] { sqlPath }));
        var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);
        return GraphExporter.Build(results, includeColumns: true, tableSchemas);
    }

    /// <summary>True when some FILTERS_ON edge lands on "<table>.<column>".</summary>
    private static bool FiltersOn(GraphPayload g, string table, string column) =>
        g.Relationships.Any(r => r.Type == "FILTERS_ON" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId &&
                n.Labels.Contains("Column") &&
                string.Equals((string)n.Properties["table"], table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)n.Properties["name"], column, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when some READS_FROM edge targets this table.</summary>
    private static bool ReadsFrom(GraphPayload g, string table) =>
        g.Relationships.Any(r => r.Type == "READS_FROM" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId && n.Labels.Contains("Table") &&
                string.Equals((string)n.Properties["name"], table, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void InSelect_UnqualifiedCollidingColumn_AttributedToInnerTable()
    {
        // dbo.DeleteDesktopModule: "WHERE ModuleDefID IN (SELECT ModuleDefID FROM
        // dbo.ModuleDefinitions WHERE DesktopModuleID = @x)". Both the outer WHERE and
        // the subquery's SELECT list reference "ModuleDefID" unqualified - the real
        // DNN Platform blind ref.
        var g = BuildGraph("in-select.sql");

        Assert.True(FiltersOn(g, "dbo.moduledefinitions", "ModuleDefID"),
            "la columna ModuleDefID de la subconsulta del IN debe atribuirse a dbo.ModuleDefinitions");
        Assert.True(FiltersOn(g, "dbo.moduledefinitions", "DesktopModuleID"),
            "DesktopModuleID (sin colision de nombre) ya funcionaba - control de no regresion");
        Assert.True(ReadsFrom(g, "dbo.moduledefinitions"), "la tabla de la subconsulta debe tener su propio READS_FROM");
    }

    [Fact]
    public void NotExists_CorrelatedAndCollidingColumn_BothScopesResolved()
    {
        // dbo.PurgeOrphanPermissions: "DELETE p FROM dbo.Permission p WHERE NOT EXISTS
        // (SELECT 1 FROM dbo.ModuleDefinitions WHERE ModuleDefID = p.ModuleDefID)". The
        // unqualified inner "ModuleDefID" must land on ModuleDefinitions; the qualified
        // correlated "p.ModuleDefID" must land on Permission - not collapse into one
        // ambiguous lookup.
        var g = BuildGraph("exists.sql");

        Assert.True(FiltersOn(g, "dbo.moduledefinitions", "ModuleDefID"),
            "el ModuleDefID sin cualificar de dentro del EXISTS debe atribuirse a dbo.ModuleDefinitions");
        Assert.True(FiltersOn(g, "dbo.permission", "ModuleDefID"),
            "la referencia correlacionada Permission.ModuleDefID debe atribuirse a dbo.Permission");
    }

    [Fact]
    public void ScalarSubquery_UnqualifiedCollidingColumn_AttributedToInnerTable()
    {
        // dbo.GetDefaultLanguageFiles: "WHERE PortalID = (SELECT PortalID FROM
        // dbo.Portals WHERE DefaultLanguage = 'en-US')". "PortalID" exists on both
        // dbo.Files (outer) and dbo.Portals (inner) - same collision, scalar form.
        var g = BuildGraph("scalar.sql");

        Assert.True(FiltersOn(g, "dbo.portals", "PortalID"),
            "el PortalID de la subconsulta escalar debe atribuirse a dbo.Portals");
        Assert.True(FiltersOn(g, "dbo.portals", "DefaultLanguage"),
            "DefaultLanguage (sin colision) ya funcionaba - control de no regresion");
    }

    [Fact]
    public void DerivedTable_OwnWhereClause_AttributedToInnerTable()
    {
        // dbo.GetLocalizedPortalNames: "FROM (SELECT pl.PortalID, pl.PortalName FROM
        // dbo.PortalLocalization pl WHERE pl.CultureCode = 'en-US') portals". The
        // derived table's own WHERE (CultureCode) belongs to dbo.PortalLocalization,
        // not to the outer alias "portals".
        var g = BuildGraph("derived-table.sql");

        Assert.True(FiltersOn(g, "dbo.portallocalization", "CultureCode"),
            "el WHERE propio de la derived table debe atribuirse a dbo.PortalLocalization");
    }
}
