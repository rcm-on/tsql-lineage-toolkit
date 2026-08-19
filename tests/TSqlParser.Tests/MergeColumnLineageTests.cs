using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Causa #2 de eval/column-recall/blind-refs.md ( 23/140 ciegas del corpus DNN, 16,4 %):
/// "MERGE: ni la condicion ON ni el WHEN MATCHED THEN UPDATE SET generan arista".
///
/// Diagnostico (medido leyendo AstWalker.cs antes de tocar nada, no supuesto):
///   1. La clausula ON de un MERGE nunca pasa por ExtractFilterColumns/FILTERS_ON: el
///      comentario de esa funcion ya documentaba el hueco ("MERGE's ON clause... simply
///      yield no filter columns here") y el caso `MergeStatement` en Walk() nunca usa el
///      parametro `filterColumnsOverride` de AddLink que existe justo para este tipo de
///      caso (INSERT si lo usa). Cero aristas FILTERS_ON, siempre, para cualquier MERGE.
///   2. WHEN MATCHED THEN UPDATE SET SI emite DERIVES_FROM (via MergeLineage/AddFrom)
///      cuando el lado derecho resuelve a una tabla real - eso es lo que notes/extraction-
///      gaps.md Seccion 1.1 verifico y por eso "se sostiene" para ese caso. Pero
///      `mrgColumns` (que alimenta WRITES_COLUMN) se calculaba EXCLUSIVAMENTE a partir de
///      `mrgLineage.Select(d => d.TargetColumn)| - a diferencia de UpdateColumns() (para un
///      UPDATE normal), que lista TODAS las columnas del SET sin importar si su lineage se
///      resolvio. Cuando el USING es una tabla derivada de solo variables/parametros (sin
///      tabla base real, patron real de dbo.UpdateHostSetting en DNN), AddFrom no encuentra
///      ninguna tabla que resolver y la columna destino desaparece del todo: ni
///      DERIVES_FROM ni WRITES_COLUMN. Ese es el hueco real, no "MERGE UPDATE SET no
///      funciona nunca" como sugeria la clasificacion de causa #2 en bruto.
///
/// Los dos casos de abajo reproducen ambas mitades por separado, con fixtures en
/// eval/community-edge-cases/merge-on-and-update/ (uno con USING sobre una tabla real,
/// otro con USING sobre una tabla derivada, el patron exacto de UpdateHostSetting).
/// </summary>
public class MergeColumnLineageTests
{
    private const string Db = "TestDb";

    /// <summary>Sube desde el bin de tests hasta la raiz del toolkit (la carpeta que contiene eval/community-edge-cases).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "community-edge-cases", "run.mjs")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontro eval/community-edge-cases/run.mjs subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(RepoRoot(), "eval", "community-edge-cases", "merge-on-and-update", fileName);

    private static GraphPayload BuildGraphFromFixture(string fileName, string objectName)
    {
        var sql = File.ReadAllText(FixturePath(fileName));
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    private static IEnumerable<GraphRel> FindRels(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.Where(r => r.Type == type && (extra == null || extra(r)));

    private static GraphNode? FindColumn(GraphPayload graph, string table, string name) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == table && (string)n.Properties["name"] == name);

    private static GraphNode? FindStep(GraphPayload graph, string action) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Step") && (string)n.Properties["action"] == action);

    // ------------------------------------------------------------------
    // Mitad 1: la condicion ON, con columnas de las DOS tablas (target real + source real).
    // ------------------------------------------------------------------

    [Fact]
    public void MergeOn_BothTablesColumns_EmitFiltersOn()
    {
        var graph = BuildGraphFromFixture("merge-on-both-sides.sql", "dbo.usp_MergeOnBothSides");
        var step = FindStep(graph, "MERGE");
        Assert.NotNull(step);

        var targetCode = FindColumn(graph, "dbo.targetsettings", "Code");
        var targetRegion = FindColumn(graph, "dbo.targetsettings", "Region");
        var sourceCode = FindColumn(graph, "dbo.sourcesettings", "Code");
        var sourceRegion = FindColumn(graph, "dbo.sourcesettings", "Region");
        Assert.NotNull(targetCode);
        Assert.NotNull(targetRegion);
        Assert.NotNull(sourceCode);
        Assert.NotNull(sourceRegion);

        // "S.Code = Q.Code AND S.Region = Q.Region": ambas mitades del ON, target y source.
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == targetCode!.Id));
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == targetRegion!.Id));
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == sourceCode!.Id));
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == sourceRegion!.Id));
    }

    /// <summary>Regresion: el ON no debe robarle nada al lineage de columna del UPDATE SET/INSERT que ya funcionaba.</summary>
    [Fact]
    public void MergeOn_DoesNotRegressExistingUpdateSetLineage()
    {
        var graph = BuildGraphFromFixture("merge-on-both-sides.sql", "dbo.usp_MergeOnBothSides");

        var targetValue = FindColumn(graph, "dbo.targetsettings", "Value");
        var sourceValue = FindColumn(graph, "dbo.sourcesettings", "Value");
        Assert.NotNull(targetValue);
        Assert.NotNull(sourceValue);
        Assert.NotNull(FindRel(graph, "DERIVES_FROM", r => r.StartNodeId == targetValue!.Id && r.EndNodeId == sourceValue!.Id));
    }

    // ------------------------------------------------------------------
    // Mitad 2: WHEN MATCHED THEN UPDATE SET desde una tabla derivada de solo
    // variables/parametros (patron real de dbo.UpdateHostSetting, DNN corpus).
    // ------------------------------------------------------------------

    [Fact]
    public void MergeUpdateSet_FromDerivedParamSource_StillWritesTargetColumn()
    {
        var graph = BuildGraphFromFixture("merge-derived-source.sql", "dbo.UpdateHostSetting");
        var step = FindStep(graph, "MERGE");
        Assert.NotNull(step);

        var writesTo = FindRel(graph, "WRITES_TO", r => r.StartNodeId == step!.Id);
        Assert.NotNull(writesTo);

        var settingValue = FindColumn(graph, "dbo.hostsettings", "SettingValue");
        Assert.NotNull(settingValue);
        // El core del bug: "S.SettingValue = Q.SV" donde Q es "(SELECT @SettingName AS
        // SN, @SettingValue AS SV)" (sin tabla base real) no puede dar DERIVES_FROM (no hay
        // fuente que resolver), pero SIGUE siendo una escritura real y estatica - debe llevar
        // WRITES_COLUMN igual que UpdateColumns() ya hace para un UPDATE normal.
        Assert.NotNull(FindRel(graph, "WRITES_COLUMN", r => r.StartNodeId == writesTo!.StartNodeId && r.EndNodeId == settingValue!.Id));

        // Las otras dos columnas del mismo SET (constante/funcion, no vienen de Q) tambien
        // deben escribirse: antes del fix ninguna de las tres aparecia en el grafo.
        var lastModBy = FindColumn(graph, "dbo.hostsettings", "LastModifiedByUserID");
        var lastModOn = FindColumn(graph, "dbo.hostsettings", "LastModifiedOnDate");
        Assert.NotNull(lastModBy);
        Assert.NotNull(lastModOn);
        Assert.NotNull(FindRel(graph, "WRITES_COLUMN", r => r.StartNodeId == writesTo!.StartNodeId && r.EndNodeId == lastModBy!.Id));
        Assert.NotNull(FindRel(graph, "WRITES_COLUMN", r => r.StartNodeId == writesTo!.StartNodeId && r.EndNodeId == lastModOn!.Id));
    }

    [Fact]
    public void MergeUpdateSet_FromDerivedParamSource_NoFabricatedDerivesFrom()
    {
        // Invariante de precision: Q (la tabla derivada) no es una tabla real, asi que no
        // debe fabricarse ningun DERIVES_FROM que apunte a "Q" como si fuera una tabla base -
        // el motor no debe adivinar. (No hay tabla Q en el grafo en absoluto.)
        var graph = BuildGraphFromFixture("merge-derived-source.sql", "dbo.UpdateHostSetting");
        var phantomQ = graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Table")
            && ((string)n.Properties["name"]).Equals("Q", StringComparison.OrdinalIgnoreCase));
        Assert.Null(phantomQ);
    }

    [Fact]
    public void MergeOn_DerivedSource_TargetColumnStillFiltersOn()
    {
        // "ON (S.SettingName = Q.SN)": S.SettingName (columna real del target) debe aparecer
        // en FILTERS_ON aunque Q.SN (tabla derivada) no pueda resolverse a nada.
        var graph = BuildGraphFromFixture("merge-derived-source.sql", "dbo.UpdateHostSetting");
        var step = FindStep(graph, "MERGE");
        Assert.NotNull(step);

        var settingName = FindColumn(graph, "dbo.hostsettings", "SettingName");
        Assert.NotNull(settingName);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == settingName!.Id));
    }
}
