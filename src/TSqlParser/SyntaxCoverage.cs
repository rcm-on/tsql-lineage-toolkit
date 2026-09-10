using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>Un tipo de nodo que el recorrido encontró y no supo tratar, con las veces que apareció.</summary>
public readonly record struct UncoveredNode(string Module, string Family, string NodeType, int Count, bool Benign);

/// <summary>
/// Cobertura sintáctica: qué construcciones llegan al <c>default</c> del recorrido.
///
/// Sin esto un tipo de nodo sin caso no produce error ni ciega — produce un grafo incompleto que
/// parece completo. El sumidero es por hilo y no se activa salvo dentro de <see cref="Collect"/>,
/// así que el camino normal del motor no paga nada.
/// </summary>
public static class SyntaxCoverage
{
    public const string FamiliaSentencia = "statement";
    public const string FamiliaTabla = "table_reference";

    [ThreadStatic] private static Dictionary<string, int>? _sumidero;

    /// <summary>
    /// Tipos que caen al <c>default</c> por diseño y no afectan al lineage. Estar aquí no es
    /// "no pasa nada": es una afirmación revisable de que esa construcción no mueve datos.
    /// </summary>
    private static readonly HashSet<string> Benignos = new(StringComparer.Ordinal)
    {
        // Sentencias: opciones de sesión, permisos, mantenimiento y ruido de control.
        "SetOptionStatement", "PredicateSetStatement", "SetTransactionIsolationLevelStatement",
        "SetRowCountStatement", "SetIdentityInsertStatement", "UseStatement", "LabelStatement",
        "ExecuteAsStatement", "RevertStatement", "GrantStatement", "DenyStatement", "RevokeStatement",
        "UpdateStatisticsStatement", "CreateStatisticsStatement", "AlterIndexStatement",
        "DropIndexStatement", "CreateSchemaStatement", "DeclareCursorStatement",
        "SetUserStatement", "CheckpointStatement", "DBCCStatement",
        // Referencias de tabla dejadas sin registrar a propósito (ver CollectTableRefsInto).
        "BulkOpenRowset",
    };

    /// <summary>Abre un ámbito de recolección para el hilo actual.</summary>
    public static Scope Collect() => new();

    internal static void Record(string family, TSqlFragment node)
    {
        if (_sumidero is null) return;
        var clave = family + "|" + node.GetType().Name;
        _sumidero[clave] = _sumidero.TryGetValue(clave, out var n) ? n + 1 : 1;
    }

    public sealed class Scope : IDisposable
    {
        private readonly Dictionary<string, int>? _anterior;
        private readonly Dictionary<string, int> _propio = new(StringComparer.Ordinal);

        internal Scope()
        {
            _anterior = _sumidero;
            _sumidero = _propio;
        }

        /// <summary>Lo recogido hasta ahora, como filas atribuidas a <paramref name="module"/>.</summary>
        public IReadOnlyList<UncoveredNode> Rows(string module) =>
            _propio.Select(kv =>
                {
                    var partes = kv.Key.Split('|', 2);
                    return new UncoveredNode(module, partes[0], partes[1], kv.Value, Benignos.Contains(partes[1]));
                })
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.NodeType, StringComparer.Ordinal)
                .ToList();

        public void Dispose() => _sumidero = _anterior;
    }

    /// <summary>
    /// Reanaliza cada módulo de un input.json por separado para poder atribuirle lo que no se
    /// cubrió. Sin catálogo de columnas a propósito: aquí se cuentan tipos de nodo, no lineage.
    /// </summary>
    public static IReadOnlyList<UncoveredNode> Analyze(string inputJsonPath)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sources = JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(inputJsonPath), opts)
            ?? throw new InvalidDataException("No se pudo leer el input JSON: " + inputJsonPath);

        var filas = new List<UncoveredNode>();
        foreach (var src in sources)
        {
            using var scope = Collect();
            SqlAnalyzer.AnalyzeObject(src.Name, src.Sql);
            filas.AddRange(scope.Rows(ModuloDe(src.Name)));
        }
        return filas;
    }

    /// <summary>Una fila del informe anónimo: el tipo, cuántas veces y en cuántos módulos. Sin nombres.</summary>
    public sealed record AnonNodeRow(string Family, string NodeType, int Occurrences, int Modules, bool Benign);

    /// <summary>Un error de parseo por su NÚMERO de ScriptDom. El mensaje lleva el identificador que rompió; el número no.</summary>
    public sealed record AnonParseError(int Number, int Modules);

    /// <summary>
    /// Diagnóstico que puede salir de una red ajena sin anonimizar nada, porque no lleva nada que
    /// anonimizar: ni nombres, ni SQL, ni estructura del modelo. Solo tipos de nodo de ScriptDom,
    /// números de error y recuentos. Basta para arreglar el parseo, que es de lo que trata.
    /// </summary>
    public sealed record AnonReport(
        string Schema,
        string Generated,
        int ModulesTotal,
        int ModulesWithParseError,
        IReadOnlyList<AnonNodeRow> Uncovered,
        IReadOnlyList<AnonParseError> ParseErrors);

    public static AnonReport AnalyzeAnonymous(string inputJsonPath)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sources = JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(inputJsonPath), opts)
            ?? throw new InvalidDataException("No se pudo leer el input JSON: " + inputJsonPath);

        var filas = new List<UncoveredNode>();
        var erroresPorNumero = new Dictionary<int, HashSet<string>>();
        var conError = 0;

        foreach (var src in sources)
        {
            var modulo = ModuloDe(src.Name);
            using var scope = Collect();
            var res = SqlAnalyzer.AnalyzeObject(src.Name, src.Sql);
            filas.AddRange(scope.Rows(modulo));

            if (res.Error == null) continue;
            conError++;
            foreach (var n in res.ParseErrorNumbers)
            {
                if (!erroresPorNumero.TryGetValue(n, out var mods))
                    erroresPorNumero[n] = mods = new HashSet<string>(StringComparer.Ordinal);
                mods.Add(modulo);
            }
        }

        var agregadas = filas
            .GroupBy(f => (f.Family, f.NodeType, f.Benign))
            .Select(g => new AnonNodeRow(
                g.Key.Family, g.Key.NodeType,
                g.Sum(f => f.Count),
                g.Select(f => f.Module).Distinct(StringComparer.Ordinal).Count(),
                g.Key.Benign))
            .OrderBy(r => r.Benign)
            .ThenByDescending(r => r.Modules)
            .ThenByDescending(r => r.Occurrences)
            .ToList();

        return new AnonReport(
            Schema: "tsql-diag/1",
            Generated: DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ModulesTotal: sources.Count,
            ModulesWithParseError: conError,
            Uncovered: agregadas,
            ParseErrors: erroresPorNumero
                .Select(kv => new AnonParseError(kv.Key, kv.Value.Count))
                .OrderByDescending(e => e.Modules)
                .ToList());
    }

    public static void WriteAnonJson(AnonReport report, string outputPath)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json, Utf8Io.NoBom);
    }

    /// <summary>
    /// Comprobación que un humano puede repetir a ojo: TODO texto del informe tiene que ser
    /// vocabulario cerrado — el identificador de esquema, una fecha, el nombre de una familia, o
    /// un tipo que existe de verdad en el ensamblado de ScriptDom de Microsoft. Un nombre que
    /// venga de la base del usuario no puede pasar esa última condición, porque no es un tipo del
    /// parser. Devuelve la lista de textos que NO superan la comprobación: vacía = limpio.
    /// </summary>
    public static IReadOnlyList<string> VerifyAnonymous(AnonReport r)
    {
        var ensamblado = typeof(TSqlFragment).Assembly;
        var sospechosos = new List<string>();

        if (r.Schema != "tsql-diag/1") sospechosos.Add(r.Schema);
        if (!DateTime.TryParse(r.Generated, out _)) sospechosos.Add(r.Generated);

        foreach (var u in r.Uncovered)
        {
            if (u.Family != FamiliaSentencia && u.Family != FamiliaTabla)
                sospechosos.Add(u.Family);
            if (ensamblado.GetType("Microsoft.SqlServer.TransactSql.ScriptDom." + u.NodeType) == null)
                sospechosos.Add(u.NodeType);
        }

        return sospechosos;
    }

    /// <summary>El informe entero como texto plano, para leerlo completo antes de que salga de la red.</summary>
    public static string RenderAnon(AnonReport r)
    {
        var lineas = new List<string>
        {
            $"esquema           {r.Schema}",
            $"generado          {r.Generated}",
            $"modulos           {r.ModulesTotal}",
            $"con error parseo  {r.ModulesWithParseError}",
            "",
            "nodos sin cubrir (familia / tipo / apariciones / modulos / benigno)",
        };

        foreach (var u in r.Uncovered)
            lineas.Add($"  {u.Family,-16} {u.NodeType,-38} {u.Occurrences,6} {u.Modules,6}  {(u.Benign ? "si" : "NO")}");

        if (r.Uncovered.Count == 0)
            lineas.Add("  (ninguno)");

        lineas.Add("");
        lineas.Add("errores de parseo (numero de ScriptDom / modulos)");
        foreach (var e in r.ParseErrors)
            lineas.Add($"  {e.Number,-8} {e.Modules}");
        if (r.ParseErrors.Count == 0)
            lineas.Add("  (ninguno)");

        return string.Join(Environment.NewLine, lineas);
    }

    public static string SummarizeAnon(AnonReport r, string outputPath)
    {
        var noBenignas = r.Uncovered.Where(u => !u.Benign).ToList();
        var lineas = new List<string>
        {
            $"syntax-coverage --anon: {r.ModulesTotal} modulo(s), {r.ModulesWithParseError} con error de parseo, " +
            $"{noBenignas.Count} tipo(s) de nodo sin cubrir de {r.Uncovered.Count} listado(s) " +
            $"({r.Uncovered.Count - noBenignas.Count} benigno(s) declarado(s)) -> {outputPath}",
            "  El fichero NO contiene nombres, ni SQL, ni estructura: solo tipos de nodo, numeros de error y recuentos.",
        };
        foreach (var u in noBenignas.Take(15))
            lineas.Add($"  {u.NodeType} ({u.Family}): {u.Occurrences} aparicion(es) en {u.Modules} modulo(s)");
        foreach (var e in r.ParseErrors.Take(10))
            lineas.Add($"  error de parseo {e.Number}: {e.Modules} modulo(s)");
        return string.Join(Environment.NewLine, lineas);
    }

    internal static string ModuloDe(string name)
    {
        var i = name.IndexOf("::", StringComparison.Ordinal);
        return (i >= 0 ? name[(i + 2)..] : name).ToLowerInvariant();
    }

    public static void WriteCsv(IReadOnlyList<UncoveredNode> rows, string outputPath)
    {
        using var w = new StreamWriter(outputPath, append: false, Utf8Io.NoBom);
        w.WriteLine("module,family,node_type,count,benign");
        foreach (var r in rows.OrderBy(r => r.Benign).ThenByDescending(r => r.Count).ThenBy(r => r.Module, StringComparer.Ordinal))
            w.WriteLine($"{Csv(r.Module)},{r.Family},{r.NodeType},{r.Count},{(r.Benign ? "si" : "no")}");
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    /// <summary>
    /// Resumen accionable: agrupa por tipo de nodo, primero lo no benigno. Un tipo que aparece
    /// en muchos módulos es una familia entera de lineage perdido, no un caso raro.
    /// </summary>
    public static string Summarize(IReadOnlyList<UncoveredNode> rows, string outputPath)
    {
        var noCubiertos = rows.Where(r => !r.Benign).ToList();
        var lineas = new List<string>
        {
            $"syntax-coverage: {rows.Select(r => r.Module).Distinct(StringComparer.Ordinal).Count()} modulo(s) con nodos sin cubrir, " +
            $"{noCubiertos.Count} fila(s) NO benignas -> {outputPath}",
        };

        foreach (var g in noCubiertos
                     .GroupBy(r => (r.Family, r.NodeType))
                     .OrderByDescending(g => g.Sum(r => r.Count))
                     .Take(15))
        {
            lineas.Add($"  {g.Key.NodeType} ({g.Key.Family}): {g.Sum(r => r.Count)} aparicion(es) en " +
                       $"{g.Select(r => r.Module).Distinct(StringComparer.Ordinal).Count()} modulo(s)");
        }

        if (noCubiertos.Count == 0)
            lineas.Add("  Ningun nodo sin cubrir fuera de los benignos declarados. " +
                       "Que no significa que el lineage sea correcto: significa que el recorrido no se salto nada por falta de caso.");

        return string.Join(Environment.NewLine, lineas);
    }
}
