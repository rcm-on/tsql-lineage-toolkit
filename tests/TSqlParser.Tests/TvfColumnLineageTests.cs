using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Causa #3 de eval/column-recall/blind-refs.md ( 17/140 ciegas del corpus DNN, 12,1 %):
/// "Funcion de tabla (TVF) invocada como origen de filas (FROM func(...), CROSS/OUTER
/// APPLY, JOIN func(...)) - columnas de salida no resueltas".
///
/// Root cause: AstWalker.CollectTableRefsInto solo registraba un
/// SchemaObjectFunctionTableReference como tabla cuando su esquema era "sys" (TVF de
/// catalogo); un TVF de usuario caia sin registrar y "alias.col" nunca resolvia. Un
/// segundo hueco en GraphExporter: cuando el TVF SI tiene su propio SqlObject analizado,
/// el step de la llamada emitia TARGETS (referencia a objeto) en vez de READS_FROM, y solo
/// una VIEW conseguia READS_COLUMN sobre sus propias columnas de salida en ese branch - un
/// TVF no. Un tercer hueco (InputAnalyzer): "SELECT * FROM func()" no expandia porque
/// nadie conocia las columnas de salida del TVF (a diferencia de una vista).
///
/// Cubre ambas formas de TVF por separado (inline: columnas por posicion del SELECT list;
/// multi-sentencia: columnas declaradas en RETURNS @t TABLE(...)).
/// </summary>
public class TvfColumnLineageTests
{
    private const string Db = "TestDb";

    /// <summary>Sube desde el bin de tests hasta la raiz del toolkit.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", "community-edge-cases", "run.mjs")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir == null, "No se encontro eval/community-edge-cases/run.mjs subiendo desde " + AppContext.BaseDirectory);
        return dir!;
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(RepoRoot(), "eval", "community-edge-cases", "tvf-row-source", fileName);

    private static ObjectResult Analyze(string fileName, string objectName, IReadOnlyDictionary<string, List<string>>? tableColumns = null)
    {
        var sql = File.ReadAllText(FixturePath(fileName));
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::{objectName}", sql, tableColumns);
        Assert.Null(result.Error);
        return result;
    }

    private static GraphRel? FindRel(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.FirstOrDefault(r => r.Type == type && (extra == null || extra(r)));

    private static IEnumerable<GraphRel> FindRels(GraphPayload graph, string type, Func<GraphRel, bool>? extra = null) =>
        graph.Relationships.Where(r => r.Type == type && (extra == null || extra(r)));

    private static GraphNode? FindColumn(GraphPayload graph, string table, string name) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Column")
            && (string)n.Properties["table"] == table && (string)n.Properties["name"] == name);

    private static GraphNode? FindStep(GraphPayload graph, string objectName, string action) =>
        graph.Nodes.FirstOrDefault(n => n.Labels.Contains("Step")
            && n.Id.StartsWith($"{Db}::{objectName}#step", StringComparison.Ordinal)
            && (string)n.Properties["action"] == action);

    // ------------------------------------------------------------------
    // "FROM func(...) AS alias" - TVF en linea, columna cualificada en WHERE.
    // ------------------------------------------------------------------

    [Fact]
    public void FromInlineTvf_QualifiedColumnInWhere_EmitsFiltersOn()
    {
        var caller = Analyze("caller-from-inline-tvf.sql", "dbo.CountActiveRoleForUser");
        var graph = GraphExporter.Build(new List<ObjectResult> { caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.CountActiveRoleForUser", "SELECT");
        Assert.NotNull(step);

        var item = FindColumn(graph, "dbo.fn_activeroleids", "Item");
        Assert.NotNull(item);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == item!.Id));
    }

    // ------------------------------------------------------------------
    // "JOIN func(...) AS alias ON x = alias.col" - TVF multi-sentencia, dentro de un
    // INSERT...SELECT (patron exacto de dbo.CoreMessaging_CreateMessageRecipientsForRole).
    // ------------------------------------------------------------------

    [Fact]
    public void JoinMultiStatementTvf_OnPredicateColumn_ResolvesInsteadOfVanishing()
    {
        var caller = Analyze("caller-join-multistatement-tvf.sql", "dbo.CreateMessageRecipientsForRole");
        var graph = GraphExporter.Build(new List<ObjectResult> { caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.CreateMessageRecipientsForRole", "INSERT");
        Assert.NotNull(step);

        var item = FindColumn(graph, "dbo.splitstrings_cte", "Item");
        Assert.NotNull(item);
        Assert.NotNull(FindRel(graph, "FILTERS_ON", r => r.StartNodeId == step!.Id && r.EndNodeId == item!.Id));

        // La union con el TVF tambien debe dar READS_FROM sobre el TVF (no solo el JOIN
        // predicate) - la mecanica normal de un JOIN extra.
        Assert.NotNull(FindRel(graph, "READS_FROM", r => r.StartNodeId == step!.Id
            && graph.Nodes.Any(n => n.Id == r.EndNodeId && n.Labels.Contains("Table")
                && (string)n.Properties["name"] == "dbo.SplitStrings_CTE")));
    }

    // ------------------------------------------------------------------
    // "CROSS APPLY func(...) AS alias" - TVF multi-sentencia, columnas cualificadas en el
    // SELECT list junto a columnas de una tabla real.
    // ------------------------------------------------------------------

    [Fact]
    public void CrossApplyMultiStatementTvf_SelectListColumns_ResolveAsReads()
    {
        var caller = Analyze("caller-cross-apply-multistatement-tvf.sql", "dbo.ListAssemblyVersions");
        var graph = GraphExporter.Build(new List<ObjectResult> { caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.ListAssemblyVersions", "SELECT");
        Assert.NotNull(step);

        var major = FindColumn(graph, "dbo.fn_parseversion", "Major");
        var minor = FindColumn(graph, "dbo.fn_parseversion", "Minor");
        var build = FindColumn(graph, "dbo.fn_parseversion", "Build");
        Assert.NotNull(major);
        Assert.NotNull(minor);
        Assert.NotNull(build);
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == major!.Id));
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == minor!.Id));
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == build!.Id));

        // Regresion: la tabla real (dbo.Assemblies) del FROM sigue leyendose normal.
        var assemblyName = FindColumn(graph, "dbo.assemblies", "AssemblyName");
        Assert.NotNull(assemblyName);
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == assemblyName!.Id));
    }

    // ------------------------------------------------------------------
    // "SELECT @v1 = Col1, @v2 = Col2 FROM func(...)" (sin alias) - el TVF tiene su propio
    // SqlObject analizado, asi que el step apunta TARGETS a ese objeto: sin el fix de
    // GraphExporter, esa rama solo daba READS_COLUMN para VIEWs, nunca para un TVF.
    // ------------------------------------------------------------------

    [Fact]
    public void VariableAssignmentFromMultiStatementTvf_EmitsReadsColumnOnTvfObject()
    {
        // Con las DOS objetos analizados a la vez (como hace InputAnalyzer sobre el
        // corpus real), fn_ParseVersion tiene su propio :SqlObject y el step de
        // fn_CompareVersion apunta TARGETS a el en vez de READS_FROM a una tabla suelta -
        // justo el escenario que exponia el hueco de GraphExporter.
        var callee = Analyze("fn-parseversion.sql", "dbo.fn_ParseVersion");
        var caller = Analyze("caller-variable-assignment-tvf.sql", "dbo.fn_CompareVersion");
        var graph = GraphExporter.Build(new List<ObjectResult> { callee, caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.fn_CompareVersion", "SELECT");
        Assert.NotNull(step);

        // El paso SIGUE apuntando TARGETS al :SqlObject de fn_ParseVersion (no se pierde
        // esa arista), y ADEMAS ahora tambien da READS_COLUMN sobre sus columnas.
        Assert.NotNull(FindRel(graph, "TARGETS", r => r.StartNodeId == step!.Id
            && r.EndNodeId == $"{Db}::dbo.fn_ParseVersion"));

        var major = FindColumn(graph, "dbo.fn_parseversion", "Major");
        var minor = FindColumn(graph, "dbo.fn_parseversion", "Minor");
        var build = FindColumn(graph, "dbo.fn_parseversion", "Build");
        Assert.NotNull(major);
        Assert.NotNull(minor);
        Assert.NotNull(build);
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == major!.Id));
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == minor!.Id));
        Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == build!.Id));
    }

    // ------------------------------------------------------------------
    // "SELECT * FROM func(...)" - necesita conocer las columnas de salida del TVF
    // (InputAnalyzer TVF pre-pass -> TvfOutputColumns -> tableColumns), igual que
    // "SELECT * FROM view".
    // ------------------------------------------------------------------

    [Fact]
    public void SelectStarFromMultiStatementTvf_ExpandsUsingDeclaredReturnColumns()
    {
        // Simula lo que hace InputAnalyzer.Analyze: primero analiza el TVF para obtener
        // TvfOutputColumns (de su RETURNS @t TABLE(...) declarado), luego lo registra en
        // el catalogo tableColumns que recibe el analisis del llamante.
        var fn = Analyze("fn-parseversion.sql", "dbo.fn_ParseVersion");
        Assert.Equal(new[] { "Major", "Minor", "Build" }, fn.TvfOutputColumns);

        var tableColumns = new Dictionary<string, List<string>>
        {
            [$"{Db}::dbo.fn_parseversion"] = fn.TvfOutputColumns,
        };
        var caller = Analyze("caller-star-tvf.sql", "dbo.ShowParsedVersion", tableColumns);
        var graph = GraphExporter.Build(new List<ObjectResult> { caller }, includeColumns: true);

        var step = FindStep(graph, "dbo.ShowParsedVersion", "SELECT");
        Assert.NotNull(step);

        foreach (var colName in new[] { "Major", "Minor", "Build" })
        {
            var col = FindColumn(graph, "dbo.fn_parseversion", colName);
            Assert.NotNull(col);
            Assert.NotNull(FindRel(graph, "READS_COLUMN", r => r.StartNodeId == step!.Id && r.EndNodeId == col!.Id
                && r.Properties.TryGetValue("resolution", out var res) && (string)res == "star_expanded"));
        }
    }

    // ------------------------------------------------------------------
    // Diferencia inline vs multi-sentencia: TvfOutputColumns se calcula correctamente
    // para las dos formas, por vias distintas (SELECT list posicional vs tabla declarada).
    // ------------------------------------------------------------------

    [Fact]
    public void TvfOutputColumns_InlineForm_ComesFromSelectList()
    {
        var fn = Analyze("fn-active-role-ids.sql", "dbo.fn_ActiveRoleIds");
        Assert.Equal("INLINE_TABLE_FUNCTION", fn.ObjectType);
        Assert.Equal(new[] { "Item" }, fn.TvfOutputColumns);
    }

    [Fact]
    public void TvfOutputColumns_MultiStatementForm_ComesFromDeclaredReturnTable()
    {
        var fn = Analyze("splitstrings-cte.sql", "dbo.SplitStrings_CTE");
        Assert.Equal("TABLE_VALUED_FUNCTION", fn.ObjectType);
        Assert.Equal(new[] { "Item" }, fn.TvfOutputColumns);
    }
}
