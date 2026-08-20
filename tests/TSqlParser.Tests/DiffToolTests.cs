using System.Text.Json;
using Parser.Mcp;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Cubre la herramienta MCP diff_impact (<see cref="DiffQueries.Diff"/>): la envoltura
/// delgada, con total/truncated/reason, alrededor de <see cref="Parser.Graph.ChangeMapDiff"/>
/// (ya probado a fondo en <see cref="ChangeMapDiffTests"/>). Aquí solo se verifica la forma
/// de salida y los casos de error propios de la herramienta - no se reprueba el algoritmo
/// de diff en sí.
/// </summary>
public class DiffToolTests : IDisposable
{
    private const string Db = "TestDb";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "diff-tool-tests", Guid.NewGuid().ToString("n"));
        _tempDirs.Add(dir);
        return dir;
    }

    private string WriteStore(params (string Name, string Sql)[] objects)
    {
        var results = objects
            .Select(o => SqlAnalyzer.AnalyzeObject($"{Db}::{o.Name}", o.Sql))
            .ToList();
        foreach (var r in results)
            Assert.Null(r.Error);
        var graph = GraphExporter.Build(results, includeColumns: false);
        var dir = NewTempDir();
        NodeStoreExporter.Write(graph, dir, Db, JsonOptions);
        return dir;
    }

    // ── forma de salida ──────────────────────────────────────────────────────

    [Fact]
    public void NuevoImpactoPorDatos_SaleEnEntriesConNewlyAffectedYRiskNote()
    {
        var before = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));
        var after = WriteStore(
            ("dbo.Writer", "CREATE PROCEDURE dbo.Writer AS BEGIN INSERT INTO dbo.T (Id) VALUES (1); END"),
            ("dbo.ReaderOne", "CREATE PROCEDURE dbo.ReaderOne AS BEGIN SELECT Id FROM dbo.T; END"));

        var r = DiffQueries.Diff(before, after, limit: 20);

        var objectsChanged = (Dictionary<string, object?>)r["objects_changed"]!;
        Assert.Equal(new[] { $"{Db}::dbo.Writer" }, (List<string>)objectsChanged["items"]!);
        Assert.Equal(1, (int)objectsChanged["total"]!);
        Assert.DoesNotContain("truncated", objectsChanged.Keys);

        var newImpact = (Dictionary<string, object?>)r["new_impact"]!;
        var entries = (List<object>)newImpact["entries"]!;
        var writerEntry = (Dictionary<string, object?>)entries.Single();
        Assert.Equal($"{Db}::dbo.Writer", writerEntry["object"]);
        var viaDataAdded = (List<object>)writerEntry["via_data_added"]!;
        var t = (Dictionary<string, object?>)viaDataAdded.Single();
        Assert.EndsWith("dbo.T", (string)t["table"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "dbo.ReaderOne" }, (List<string?>)t["consumers"]!);
        Assert.Contains("dbo.ReaderOne", (List<string>)writerEntry["newly_affected"]!);

        Assert.True((int)r["newly_affected_total"]! >= 1);
        Assert.Contains("dbo", (string)r["risk_note"]!);
        Assert.DoesNotContain("reason", newImpact.Keys); // hay impacto nuevo -> no hace falta explicarlo
    }

    [Fact]
    public void NuevoImpactoPorLlamada_ConDepthYWorkflowNuevo()
    {
        var before = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 2 AS Y; END"));
        var after = WriteStore(
            ("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END"),
            ("dbo.ProcB", "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 2 AS Y; END"));

        var r = DiffQueries.Diff(before, after, limit: 20);

        var newImpact = (Dictionary<string, object?>)r["new_impact"]!;
        var entries = (List<object>)newImpact["entries"]!;
        var entry = (Dictionary<string, object?>)entries.Single();
        var viaCallsAdded = (List<object>)entry["via_calls_added"]!;
        var call = (Dictionary<string, object?>)viaCallsAdded.Single();
        Assert.Equal("dbo.ProcB", call["object"]);
        Assert.Equal(1, (int)call["depth"]!);
        Assert.False((bool)call["conditional"]!);

        var workflowsAdded = (Dictionary<string, object?>)r["workflows_added"]!;
        Assert.Contains("dbo.ProcA", (List<string>)workflowsAdded["items"]!);
    }

    [Fact]
    public void ObjetosAñadidosYEliminados_SinRuidoEnLosDemas()
    {
        var stableSql = "CREATE PROCEDURE dbo.ProcStable AS BEGIN SELECT 1 AS X; END";
        var before = WriteStore(
            ("dbo.ProcStable", stableSql),
            ("dbo.ProcGone", "CREATE PROCEDURE dbo.ProcGone AS BEGIN SELECT 9 AS Z; END"));
        var after = WriteStore(
            ("dbo.ProcStable", stableSql),
            ("dbo.ProcNew", "CREATE PROCEDURE dbo.ProcNew AS BEGIN SELECT 8 AS W; END"));

        var r = DiffQueries.Diff(before, after, limit: 20);

        Assert.Equal(new[] { $"{Db}::dbo.ProcNew" },
            (List<string>)((Dictionary<string, object?>)r["objects_added"]!)["items"]!);
        Assert.Equal(new[] { $"{Db}::dbo.ProcGone" },
            (List<string>)((Dictionary<string, object?>)r["objects_removed"]!)["items"]!);
        Assert.Equal(0, (int)((Dictionary<string, object?>)r["objects_changed"]!)["total"]!);
    }

    // ── regla del cero culpable: reason siempre presente cuando no hay impacto nuevo ──

    [Fact]
    public void StoresIdenticos_ReasonExplicaQueNoHayNadaQueComparar()
    {
        var before = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));
        var after = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));

        var r = DiffQueries.Diff(before, after, limit: 20);

        var newImpact = (Dictionary<string, object?>)r["new_impact"]!;
        Assert.Empty((List<object>)newImpact["entries"]!);
        Assert.Equal(0, (int)r["newly_affected_total"]!);
        Assert.Contains("idénticos", (string)newImpact["reason"]!);
        Assert.DoesNotContain("risk_note", r.Keys);
    }

    [Fact]
    public void ObjetoCambiaSinPropagar_ReasonDistingueDeStoresIdenticos()
    {
        // Gana una variable local no usada (content_hash distinto -> objects_changed)
        // pero no gana ni pierde via_calls/via_data: el impacto no se mueve un milímetro
        // fuera de sí mismo. Un literal distinto no vale para este caso: no se refleja en
        // el object.json (no hay Step/Variable que lo capture) y el hash no se mueve.
        var before = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));
        var after = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN DECLARE @sinUsar INT; SELECT 1 AS X; END"));

        var r = DiffQueries.Diff(before, after, limit: 20);

        Assert.Equal(1, (int)((Dictionary<string, object?>)r["objects_changed"]!)["total"]!);
        var newImpact = (Dictionary<string, object?>)r["new_impact"]!;
        Assert.Empty((List<object>)newImpact["entries"]!);
        var reason = (string)newImpact["reason"]!;
        Assert.DoesNotContain("idénticos", reason);
        Assert.Contains("impacto nuevo", reason);
    }

    // ── errores: rutas inexistentes o ilegibles ─────────────────────────────

    [Fact]
    public void BeforeOAfterVacios_LanzaConMensajeClaro()
    {
        var dir = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));

        var exBefore = Assert.Throws<McpToolException>(() => DiffQueries.Diff("", dir, 20));
        Assert.Contains("'before'", exBefore.Message);

        var exAfter = Assert.Throws<McpToolException>(() => DiffQueries.Diff(dir, "", 20));
        Assert.Contains("'after'", exAfter.Message);
    }

    [Fact]
    public void DirectorioInexistente_LanzaDiciendoQueSeEsperaUnStoreNodes()
    {
        var dir = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));
        var noExiste = Path.Combine(Path.GetTempPath(), "diff-tool-tests", "no-existe-" + Guid.NewGuid().ToString("n"));

        var ex = Assert.Throws<McpToolException>(() => DiffQueries.Diff(noExiste, dir, 20));
        Assert.Contains(".nodes", ex.Message);
    }

    [Fact]
    public void StoreSinChangeMapJson_LanzaSeñalandoElFicheroQueFalta()
    {
        // manifest.json existe (NodeStoreExporter.Write siempre lo escribe) pero se borra
        // change_map.json a mano para simular un store con esquema incompleto/antiguo.
        var dir = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));
        var changeMapPath = Path.Combine(dir, "change_map.json");
        Assert.True(File.Exists(changeMapPath));
        File.Delete(changeMapPath);

        var okDir = WriteStore(("dbo.ProcA", "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT 1 AS X; END"));
        var ex = Assert.Throws<McpToolException>(() => DiffQueries.Diff(dir, okDir, 20));
        Assert.Contains("change_map.json", ex.Message);
    }

    // ── presupuesto ──────────────────────────────────────────────────────────

    [Fact]
    public void ConValoresPorDefecto_CabeEnElPresupuesto()
    {
        // 60 pares escritor/lector, muy por encima del limit por defecto (20): cada
        // escritor gana un INSERT en after hacia una tabla que su lector ya consulta, así
        // que cada uno aporta una entrada real a new_impact. El presupuesto solo se
        // sostiene porque la herramienta trunca a `limit`, no porque el diff sea pequeño.
        var antes = new List<(string, string)>();
        var despues = new List<(string, string)>();
        for (var i = 0; i < 60; i++)
        {
            antes.Add(($"dbo.Writer{i}", $"CREATE PROCEDURE dbo.Writer{i} AS BEGIN SELECT 1 AS X; END"));
            antes.Add(($"dbo.Reader{i}", $"CREATE PROCEDURE dbo.Reader{i} AS BEGIN SELECT Id FROM dbo.T{i}; END"));
            despues.Add(($"dbo.Writer{i}", $"CREATE PROCEDURE dbo.Writer{i} AS BEGIN INSERT INTO dbo.T{i} (Id) VALUES ({i}); END"));
            despues.Add(($"dbo.Reader{i}", $"CREATE PROCEDURE dbo.Reader{i} AS BEGIN SELECT Id FROM dbo.T{i}; END"));
        }
        var before = WriteStore(antes.ToArray());
        var after = WriteStore(despues.ToArray());

        var r = DiffQueries.Diff(before, after, limit: 20);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(r);

        Assert.True(bytes.Length < McpTools.ResponseBudgetBytes,
            $"diff_impact ocupó {bytes.Length} bytes, sobre el presupuesto de {McpTools.ResponseBudgetBytes}.");
    }
}
