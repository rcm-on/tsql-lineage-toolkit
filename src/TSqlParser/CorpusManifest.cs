using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSqlParser;

/// <summary>
/// Modelo de <c>eval/corpora.json</c>: la lista de corpus de evaluación con su procedencia, su
/// catálogo y los suelos medidos que los gates no pueden bajar.
///
/// Vive en el motor y no en el proyecto de tests porque hay DOS consumidores y el esquema tiene
/// que ser uno solo: el gate de recall de columna (que lee los suelos) y el comando
/// <c>corpus</c> (que regenera corpus y catálogo contra la base viva). Dos copias del esquema
/// divergirían en el primer campo nuevo, y la divergencia sería invisible hasta que un gate
/// midiera contra un manifiesto que ya no es el que la herramienta escribe.
/// </summary>
public sealed record CorpusManifest(int SchemaVersion, string? Doc, List<CorpusEntry> Corpora)
{
    public const string FileName = "corpora.json";

    /// <summary>Ruta relativa a la raíz del repositorio, tal y como aparece en la documentación.</summary>
    public const string RelPath = "eval/" + FileName;

    /// <summary>
    /// El manifiesto es un fichero que se EDITA A MANO (los suelos se suben leyendo el informe
    /// del gate), así que se serializa para que siga siendo legible: sin escapar `>` ni `&` a
    /// `>` — el codificador por defecto lo hace y convierte la procedencia en ruido.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Sube desde <paramref name="startDir"/> buscando <c>eval/corpora.json</c>. Devuelve la raíz
    /// del repositorio (el directorio que CONTIENE <c>eval/</c>), no la ruta del fichero: todas
    /// las rutas del manifiesto son relativas a esa raíz.
    /// </summary>
    public static string? FindRepoRoot(string startDir)
    {
        var dir = Path.GetFullPath(startDir);
        while (dir != null && !File.Exists(Path.Combine(dir, "eval", FileName)))
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    public static CorpusManifest Load(string repoRoot) =>
        JsonSerializer.Deserialize<CorpusManifest>(
            File.ReadAllText(Path.Combine(repoRoot, "eval", FileName)), JsonOptions)
        ?? throw new InvalidOperationException($"{RelPath} no se pudo deserializar.");

    public CorpusEntry? Find(string id) =>
        Corpora.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <param name="Kind">
/// <c>schema-real</c> = tiene esquema propio y por tanto catálogo de columna posible; es el único
/// tipo que se gatea. <c>parser-torture</c> = código que lee DMVs y escribe en temporales (Ola
/// Hallengren, First Responder Kit): estresa el parser pero NO tiene columnas catalogadas contra
/// las que medir, así que ponerle un suelo de recall produciría un número sin significado.
/// </param>
public sealed record CorpusEntry(
    string Id,
    string Name,
    string Kind,
    string License,
    string Provenance,
    string Input,
    string? Catalog,
    string? CatalogQuery,
    CorpusSourceDb? SourceDb,
    CorpusExpected? Expected,
    CorpusFloors? Floors)
{
    public string InputPath(string repoRoot) => Resolve(repoRoot, Input);

    public string CatalogPath(string repoRoot) => Resolve(repoRoot, Catalog
        ?? throw new InvalidOperationException($"El corpus '{Id}' no declara catálogo."));

    public string CatalogQueryPath(string repoRoot) => Resolve(repoRoot, CatalogQuery
        ?? throw new InvalidOperationException($"El corpus '{Id}' no declara consulta de catálogo."));

    /// <summary>
    /// Se gatea un corpus solo si tiene esquema real, catálogo, expectativas y suelos medidos.
    ///
    /// <c>[JsonIgnore]</c> no es cosmético: es una propiedad DERIVADA, y al serializar el
    /// manifiesto (lo hace <c>corpus refresh --write</c>) aparecía como <c>"is_gated": true</c>
    /// en el fichero. Ahí engaña dos veces — parece un campo que se puede configurar, y
    /// ponerlo a <c>false</c> no tendría ningún efecto porque al releer se recalcula.
    /// </summary>
    [JsonIgnore]
    public bool IsGated => Kind == "schema-real" && Catalog != null && Floors != null && Expected != null;

    public static string Resolve(string repoRoot, string relPath) =>
        Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record CorpusSourceDb(string Name, string Server, int CompatibilityLevel);

/// <summary>
/// Invariantes de FORMA del corpus congelado, no umbrales de calidad: detectan que el fichero se
/// truncó o se regeneró contra otra base. <paramref name="CatalogRows"/> se comprueba con igualdad
/// exacta, así que actualizar un corpus obliga a tocar el manifiesto — que es justo la disciplina
/// buscada: si el corpus y el motor se mueven en el mismo commit, la cifra nueva no atribuye nada.
/// </summary>
public sealed record CorpusExpected(int CatalogRows, int MinColumnEdges);

public sealed record CorpusFloors(double StrictRecall, double LooseRecall, Dictionary<string, double> PrecisionByClass);
