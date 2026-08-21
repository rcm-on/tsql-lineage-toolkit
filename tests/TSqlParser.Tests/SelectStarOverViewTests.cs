using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// "SELECT * FROM vistaAnalizada" en un consumidor debe expandir a las columnas de
/// SALIDA de esa vista - no solo a las que la vista lee de sus tablas base (via_view,
/// mecanismo ya existente). Causa raiz medida en el corpus DNN (10 de 40 ciegas): la
/// vista SI tiene la columna en su lista SELECT, pero AstWalker.ViewColumnLineage la
/// descartaba en dos formas -
///   - funcion niladica sin ninguna columna referenciada ("dbo.SuperUserTabID() AS
///     SuperTabId": QualifiedColumnCollector.Refs queda vacio), y
///   - columna calificada por un alias de subconsulta derivada en el FROM
///     ("S.MaxSortOrder" donde S es "(SELECT ...) S": SplitColumnsByTable descarta el
///     alias porque no es una tabla real) -
/// asi que ese nombre de columna nunca llegaba al catalogo de InputAnalyzer que usa el
/// consumidor para expandir su propio "SELECT *". El fix registra el nombre de salida
/// sin fuente (SourceTable="") cuando no hay ninguna ColumnDerivation trazable, para que
/// el catalogo lo conozca aunque no haya DERIVES_FROM que dibujar.
///
/// Construye el catalogo de columnas de vista a mano (mismo patron que
/// TvfColumnLineageTests.SelectStarFromMultiStatementTvf_ExpandsUsingDeclaredReturnColumns),
/// replicando la Pasada 1 de InputAnalyzer sin depender de fixtures en eval/.
/// </summary>
public class SelectStarOverViewTests
{
    private const string Db = "TestDb";

    private static ObjectResult Analyze(string objectName, string sql, IReadOnlyDictionary<string, List<string>>? tableColumns = null)
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql, tableColumns);
        Assert.Null(result.Error);
        return result;
    }

    /// <summary>Misma catalogacion que InputAnalyzer Pasada 1: nombres de salida de la vista, sin importar si son trazables.</summary>
    private static Dictionary<string, List<string>> CatalogFor(string viewKey, ObjectResult view) => new()
    {
        [viewKey] = view.ViewColumnLineage
            .Select(d => d.TargetColumn)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    private static GraphNode? FindColumn(GraphPayload graph, string table, string name) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == table && (string)n.Properties["name"] == name);

    private static GraphNode? FindStep(GraphPayload graph, string objectName, string action) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Step")
            && n.Id.StartsWith($"{Db}::{objectName}#step", StringComparison.Ordinal)
            && (string)n.Properties["action"] == action);

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    // ------------------------------------------------------------------
    // Caso vw_Portals: columna de salida derivada de una funcion niladica
    // (sin columna referenciada -> QualifiedColumnCollector.Refs vacio).
    // ------------------------------------------------------------------

    private const string VwPortalsSql = @"
CREATE VIEW dbo.[vw_Portals]
AS
    SELECT
        P.PortalID,
        P.PortalName,
        dbo.SuperUserTabID() AS SuperTabId
    FROM dbo.[Portals] AS P
";

    private const string GetPortalsSql = @"
CREATE PROCEDURE dbo.[GetPortals]
AS
BEGIN
    SELECT * FROM dbo.[vw_Portals]
END
";

    [Fact]
    public void SelectStarOverView_ExpandsNiladicFunctionColumn_AsStarExpandedNotDirect()
    {
        var view = Analyze("dbo.vw_Portals", VwPortalsSql);
        var tableColumns = CatalogFor($"{Db}::dbo.vw_portals", view);
        var caller = Analyze("dbo.GetPortals", GetPortalsSql, tableColumns);

        var graph = GraphExporter.Build(new List<ObjectResult> { view, caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.GetPortals", "SELECT");
        Assert.NotNull(step);

        var superTabId = FindColumn(graph, "dbo.vw_portals", "SuperTabId");
        Assert.NotNull(superTabId);

        // La regla no negociable: nunca "direct" para una columna alcanzada por
        // expansion de "*", y la vista SI queda registrada como duena del nombre.
        var readsCol = FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == superTabId!.Id);
        Assert.NotNull(readsCol);
        Assert.Equal("star_expanded", (string)readsCol!.Properties["resolution"]);

        Assert.NotNull(FindRel(graph, "HAS_COLUMN", r => r.StartNodeId == $"{Db}::dbo.vw_Portals" && r.EndNodeId == superTabId!.Id));

        // No se inventa una fuente que no existe: sin DERIVES_FROM para SuperTabId.
        Assert.Null(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == superTabId!.Id));

        // Regresion: una columna trazable de la misma vista sigue funcionando como
        // antes - star_expanded en el consumidor y DERIVES_FROM en la vista.
        var portalId = FindColumn(graph, "dbo.vw_portals", "PortalID");
        Assert.NotNull(portalId);
        var portalIdRead = FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == portalId!.Id);
        Assert.NotNull(portalIdRead);
        Assert.Equal("star_expanded", (string)portalIdRead!.Properties["resolution"]);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == portalId!.Id));
    }

    // ------------------------------------------------------------------
    // Caso vw_Lists: columnas de salida calificadas por el alias de una subconsulta
    // derivada en el FROM (S.MaxSortOrder / S.EntryCount) - SplitColumnsByTable
    // descarta el alias por no ser una tabla real, así que la referencia se pierde
    // aunque SI hay columnas qualificadas (Refs.Count > 0).
    // ------------------------------------------------------------------

    private const string VwListsSql = @"
CREATE VIEW dbo.[vw_Lists]
AS
    SELECT
        L.EntryID,
        L.ListName,
        S.MaxSortOrder,
        S.EntryCount
    FROM dbo.[Lists] AS L
    LEFT JOIN (SELECT ListName, ParentID, Max(SortOrder) AS MaxSortOrder, Count(1) AS EntryCount
               FROM dbo.[Lists] GROUP BY ListName, ParentID) S
        ON L.ParentID = S.ParentID AND L.ListName = S.ListName
";

    private const string GetListEntriesSql = @"
CREATE PROCEDURE dbo.[GetListEntries]
AS
    SELECT * FROM dbo.vw_Lists
";

    [Fact]
    public void SelectStarOverView_ExpandsDerivedTableAliasColumns_AsStarExpandedNotDirect()
    {
        var view = Analyze("dbo.vw_Lists", VwListsSql);
        var tableColumns = CatalogFor($"{Db}::dbo.vw_lists", view);
        var caller = Analyze("dbo.GetListEntries", GetListEntriesSql, tableColumns);

        var graph = GraphExporter.Build(new List<ObjectResult> { view, caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.GetListEntries", "SELECT");
        Assert.NotNull(step);

        foreach (var colName in new[] { "MaxSortOrder", "EntryCount" })
        {
            var col = FindColumn(graph, "dbo.vw_lists", colName);
            Assert.NotNull(col);
            var read = FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == col!.Id);
            Assert.NotNull(read);
            Assert.Equal("star_expanded", (string)read!.Properties["resolution"]);
            Assert.Null(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == col!.Id));
        }

        // Regresion: EntryID (trazable a L, la tabla primaria) sigue con DERIVES_FROM.
        var entryId = FindColumn(graph, "dbo.vw_lists", "EntryID");
        Assert.NotNull(entryId);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == entryId!.Id));
    }
}
