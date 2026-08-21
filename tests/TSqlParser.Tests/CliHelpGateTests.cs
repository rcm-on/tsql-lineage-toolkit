using System.Text.RegularExpressions;

namespace TSqlParser.Tests;

/// <summary>
/// Todo subcomando que el CLI despacha tiene que aparecer en la ayuda.
///
/// Un subcomando que nadie puede descubrir no existe para el usuario, y el fallo es mudo:
/// el programa funciona, la ayuda no avisa de nada, simplemente omite. Paso de verdad con
/// `recall`, `corpus`, `capture-plans`, `enrich-from-plans` y `plan-summary`, que estaban
/// implementados y sin listar.
///
/// El gate lee el propio `Program.cs` en vez de ejecutar el binario: no depende de que el
/// DLL de Debug se pueda cargar (ver la trampa de Smart App Control en docs/VERIFICACION.md).
/// </summary>
public class CliHelpGateTests
{
    /// <summary>Nombres que despacha el CLI: `positional[0] == "x"` en Program.cs.</summary>
    private static readonly Regex Despachados = new(@"positional\[0\]\s*==\s*""([a-z][a-z-]*)""", RegexOptions.Compiled);

    /// <summary>
    /// Nombres del bloque de ayuda GLOBAL. Se exige la sangria de 7 espacios que usan sus
    /// lineas de continuacion: sin eso el regex tambien casaba con la linea de uso propia de
    /// cada subcomando ("Usage: TSqlParser recall ..."), y el gate daba por listado algo que
    /// solo se ve cuando ya sabes que existe. Un gate que pasa cuando no debe es peor que no
    /// tenerlo.
    /// </summary>
    private static readonly Regex EnLaAyuda = new(@"""\s{7}TSqlParser ([a-z][a-z-]*)", RegexOptions.Compiled);

    private static string LeerProgram()
    {
        var raiz = CorpusManifest.FindRepoRoot(AppContext.BaseDirectory)
                   ?? throw new InvalidOperationException("No se encontró la raíz del repo desde " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(raiz, "src", "TSqlParser", "Program.cs"));
    }

    [Fact]
    public void TodoSubcomandoDespachado_ApareceEnLaAyuda()
    {
        var programa = LeerProgram();

        var despachados = Despachados.Matches(programa).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var listados = EnLaAyuda.Matches(programa).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // Control de sensibilidad: si el regex deja de encontrar subcomandos, el test pasaría
        // vacío y no protegería nada.
        Assert.True(despachados.Count >= 8, $"Solo se detectaron {despachados.Count} subcomandos despachados; el regex se ha quedado obsoleto.");

        var sinListar = despachados.Except(listados).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(sinListar.Count == 0,
            "Subcomando(s) implementados y AUSENTES de la ayuda: " + string.Join(", ", sinListar) +
            ". Un subcomando que nadie puede descubrir no existe para el usuario: añádelo al bloque de Usage.");
    }

    [Fact]
    public void LaAyudaNoCitaSubcomandosQueNoExisten()
    {
        var programa = LeerProgram();

        var despachados = Despachados.Matches(programa).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var listados = EnLaAyuda.Matches(programa).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // La direccion contraria: una ayuda que anuncia algo inexistente es peor que una
        // incompleta, porque el usuario lo intenta y falla sin entender por que.
        var fantasmas = listados.Except(despachados)
            .Where(s => s is not ("bench-grade" or "list" or "refresh"))   // variantes internas de otro subcomando
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(fantasmas.Count == 0,
            "La ayuda cita subcomando(s) que el CLI no despacha: " + string.Join(", ", fantasmas) + ".");
    }
}
