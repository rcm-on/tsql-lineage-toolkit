namespace TSqlParser.Tests;

/// <summary>
/// Acceso desde los tests al manifiesto <c>eval/corpora.json</c>. El modelo y la carga viven en
/// <see cref="CorpusManifest"/> (motor), porque el comando <c>corpus</c> lo necesita también y el
/// esquema tiene que ser uno solo; aquí solo queda localizar la raíz del repo desde el directorio
/// de salida del test y exponer la fuente de datos de los <c>[Theory]</c>.
///
/// Por qué un manifiesto y no constantes en cada test: hasta ahora el gate de recall de columna
/// abría literalmente <c>dnn-corpus.json</c> y llevaba sus cinco suelos como <c>const</c> dentro
/// de la clase. Añadir una segunda base obligaba a duplicar la clase entera, y con ella la lógica
/// de medición — que es justo la parte que no se puede duplicar sin que las dos copias diverjan
/// en silencio y dejen de ser comparables. Con el manifiesto, añadir un corpus es añadir una
/// entrada de datos; la medición sigue siendo una sola.
/// </summary>
public static class EvalCorpora
{
    private static readonly Lazy<(string Root, CorpusManifest Manifest)> Loaded = new(() =>
    {
        var root = CorpusManifest.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No se encontró {CorpusManifest.RelPath} subiendo desde {AppContext.BaseDirectory}");
        return (root, CorpusManifest.Load(root));
    });

    /// <summary>Raíz del repositorio, localizada subiendo desde el directorio de salida del test.</summary>
    public static string RepoRoot() => Loaded.Value.Root;

    public static IReadOnlyList<CorpusEntry> All() => Loaded.Value.Manifest.Corpora;

    public static CorpusEntry Get(string id) =>
        Loaded.Value.Manifest.Find(id)
        ?? throw new InvalidOperationException(
            $"No hay corpus '{id}' en {CorpusManifest.RelPath}. Declarados: {string.Join(", ", All().Select(c => c.Id))}");

    /// <summary>
    /// Fuente de datos de los <c>[Theory]</c> que miden lineage de columna. Devuelve solo ids y no
    /// el objeto entero: xUnit sabe serializar un string, así que el nombre del caso sale legible
    /// en el runner y el fallo dice a qué corpus pertenece sin abrir el informe.
    /// </summary>
    public static TheoryData<string> GatedCorpusIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in All().Where(c => c.IsGated))
            data.Add(c.Id);
        return data;
    }

    public static string Resolve(string relPath) => CorpusEntry.Resolve(RepoRoot(), relPath);
}
