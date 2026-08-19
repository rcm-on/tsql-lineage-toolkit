namespace TSqlParser.Tests;

/// <summary>
/// Comprobaciones del propio manifiesto <c>eval/corpora.json</c>.
///
/// Con los suelos movidos a un fichero de datos aparece un modo de fallo que antes no existía:
/// un manifiesto mal escrito (ruta con errata, <c>kind</c> equivocado, lista vacía tras un
/// merge) haría que los <c>[Theory]</c> de recall se ejecutaran sobre CERO corpus. Un gate que
/// no puede fallar no es un gate — el mismo argumento que sostiene
/// <c>Measurement_IsSensitive_ControlThatMustCollapse</c>, aplicado ahora a la capa de arriba.
/// </summary>
public class EvalCorporaManifestTests
{
    [Fact]
    public void Manifest_DeclaresAtLeastOneGatedCorpus()
    {
        var gated = EvalCorpora.All().Where(c => c.IsGated).ToList();
        Assert.True(gated.Count > 0,
            "Ningún corpus del manifiesto está gateado (kind=schema-real + catalog + floors + expected). " +
            "Los Theory de recall se ejecutarían en vacío y pasarían sin medir nada. " +
            "Declarados: " + string.Join(", ", EvalCorpora.All().Select(c => $"{c.Id} (kind={c.Kind})")));
    }

    [Fact]
    public void Manifest_HasUniqueIds()
    {
        var dupes = EvalCorpora.All()
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(dupes.Count == 0, "Ids repetidos en el manifiesto: " + string.Join(", ", dupes));
    }

    /// <summary>
    /// Todo fichero referenciado debe existir. Sin esto, una ruta con errata en un corpus nuevo
    /// se manifestaría como una excepción de E/S dentro del test de medición, donde parece un
    /// fallo del motor.
    /// </summary>
    [Fact]
    public void Manifest_ReferencedFilesExist()
    {
        var missing = new List<string>();
        foreach (var c in EvalCorpora.All())
        {
            foreach (var rel in new[] { c.Input, c.Catalog, c.CatalogQuery })
            {
                if (rel is null) continue;
                if (!File.Exists(EvalCorpora.Resolve(rel)))
                    missing.Add($"{c.Id}: {rel}");
            }
        }
        Assert.True(missing.Count == 0, "Ficheros declarados que no existen:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// Un corpus con esquema real y oráculo pero SIN suelos queda fuera de los gates sin que
    /// nadie se entere: se añade el corpus, los tests siguen en verde y no se mide. Este test
    /// obliga a que esa combinación sea deliberada — o pones suelos, o marcas kind=parser-torture.
    /// </summary>
    [Fact]
    public void SchemaRealCorpora_CarryFloorsAndExpectations()
    {
        var incomplete = EvalCorpora.All()
            .Where(c => c.Kind == "schema-real" && !c.IsGated)
            .Select(c => $"{c.Id} (catalog={c.Catalog is not null}, floors={c.Floors is not null}, expected={c.Expected is not null})")
            .ToList();
        Assert.True(incomplete.Count == 0,
            "Corpus schema-real sin catálogo, suelos o expectativas — no se están gateando:\n  " +
            string.Join("\n  ", incomplete));
    }

    /// <summary>
    /// Las clases de evidencia con suelo deben ser clases que el motor emite de verdad. Un typo
    /// ("star-expanded" por "star_expanded") dejaría esa clase sin vigilar, y el test de
    /// precisión fallaría con "la clase no produjo ninguna arista" — mensaje que apunta al
    /// motor cuando el error está en el manifiesto.
    /// </summary>
    [Fact]
    public void PrecisionFloors_NameKnownEvidenceClasses()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "direct", "star_expanded", "via_view" };
        var unknown = EvalCorpora.All()
            .Where(c => c.Floors is not null)
            .SelectMany(c => c.Floors!.PrecisionByClass.Keys.Select(k => $"{c.Id}: {k}"))
            .Where(x => !known.Contains(x.Split(": ")[1]))
            .ToList();
        Assert.True(unknown.Count == 0,
            "Clases de evidencia desconocidas en los suelos (esperadas: " + string.Join("/", known) + "):\n  " +
            string.Join("\n  ", unknown));
    }
}
