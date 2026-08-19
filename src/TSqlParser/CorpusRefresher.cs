using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// Regenera los corpus de evaluación declarados en <c>eval/corpora.json</c> contra su base viva,
/// y compara el resultado con la copia congelada en el repo.
///
/// Antes de esto, "actualizar un corpus" era una receta de tres comandos de sqlcmd copiada a mano
/// del README de cada eval — es decir, no era una operación: no se podía repetir igual, no se
/// podía comprobar y, sobre todo, no había forma de enterarse de que la copia congelada se había
/// separado de la base. Aquí la operación por defecto es <b>comprobar</b> (sin escribir nada) y
/// escribir es lo que hay que pedir expresamente, porque una regeneración mueve las cifras de los
/// gates y eso tiene que ser una decisión, no un efecto secundario.
///
/// Lo que <c>--write</c> actualiza y lo que NO, a propósito:
/// <list type="bullet">
///   <item>Sí: los ficheros del corpus y <c>expected.oracle_rows</c>, que es una invariante de
///   FORMA derivada mecánicamente del fichero nuevo.</item>
///   <item>No: <c>floors</c>. Un suelo es una cifra MEDIDA corriendo el gate; copiarla desde una
///   regeneración sería fijar como invariante lo que el motor haga ese día, que es exactamente
///   como un trinquete deja de serlo.</item>
/// </list>
/// </summary>
public static class CorpusRefresher
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    /// <summary>Deriva detectada en modo comprobación: sirve como check de CI.</summary>
    public const int ExitDrift = 2;

    public static int List(string repoRoot)
    {
        var manifest = CorpusManifest.Load(repoRoot);
        Console.WriteLine($"{CorpusManifest.RelPath} (schema_version={manifest.SchemaVersion}) - {manifest.Corpora.Count} corpus\n");
        foreach (var c in manifest.Corpora)
        {
            var db = c.SourceDb is null ? "(sin base viva)" : $"{c.SourceDb.Server}/{c.SourceDb.Name}";
            Console.WriteLine($"  {c.Id,-12} {c.Kind,-15} {(c.IsGated ? "GATEADO" : "no gateado"),-11} {db}");
            Console.WriteLine($"               {c.Name}  [{c.License}]");
            Console.WriteLine($"               input:  {c.Input}  {(File.Exists(c.InputPath(repoRoot)) ? "" : "  <-- NO EXISTE")}");
            if (c.Oracle != null)
                Console.WriteLine($"               oracle: {c.Oracle} ({(File.Exists(c.OraclePath(repoRoot)) ? $"{CountLines(c.OraclePath(repoRoot))} filas" : "NO EXISTE")})");
            if (c.Floors != null)
                Console.WriteLine($"               suelos: estricto {c.Floors.StrictRecall:P2}  laxo {c.Floors.LooseRecall:P2}  " +
                                  string.Join("  ", c.Floors.PrecisionByClass.Select(kv => $"{kv.Key} {kv.Value:P2}")));
            Console.WriteLine();
        }
        return ExitOk;
    }

    /// <param name="write">
    /// Si es false (por defecto) solo compara y no toca ningún fichero del repo.
    /// </param>
    public static int Refresh(string repoRoot, string id, string? serverOverride, bool write)
    {
        var manifest = CorpusManifest.Load(repoRoot);
        var corpus = manifest.Find(id);
        if (corpus == null)
        {
            Console.Error.WriteLine($"No hay corpus '{id}' en {CorpusManifest.RelPath}. Declarados: " +
                                    string.Join(", ", manifest.Corpora.Select(c => c.Id)));
            return ExitError;
        }
        if (corpus.SourceDb == null)
        {
            Console.Error.WriteLine($"El corpus '{corpus.Id}' no declara source_db: no hay base viva desde la que regenerarlo.");
            return ExitError;
        }
        if (corpus.Oracle == null || corpus.OracleQuery == null)
        {
            Console.Error.WriteLine($"El corpus '{corpus.Id}' no declara oracle/oracle_query: no se puede regenerar su oráculo.");
            return ExitError;
        }

        var server = serverOverride ?? corpus.SourceDb.Server;
        var database = corpus.SourceDb.Name;
        Console.WriteLine($"Corpus '{corpus.Id}' <- {server}/{database}   (modo: {(write ? "ESCRITURA" : "solo comprobación")})\n");

        // El directorio temporal lleva la BASE en el nombre, no solo el id del corpus. Con solo
        // el id, regenerar el mismo corpus apuntado a otra base (lo que se hace para comprobar
        // que el comparador detecta deriva de verdad) machaca los artefactos del run bueno, y
        // luego se inspeccionan creyendo que son los del corpus. Pasó a la primera.
        var tmpDir = Path.Combine(Path.GetTempPath(), "tsql-corpus-refresh", $"{corpus.Id}-{database}");
        Directory.CreateDirectory(tmpDir);
        var newInput = Path.Combine(tmpDir, "input.json");
        var newOracle = Path.Combine(tmpDir, "oracle.psv");

        var compatWarning = CheckCompatibilityLevel(server, database, corpus.SourceDb.CompatibilityLevel);

        // Mismo camino que el comando `extract <db> <out> --tables`: definiciones de módulos y
        // luego el DDL de las tablas base, para que el input.json sea autocontenido (sin él, el
        // motor no puede expandir un SELECT * y el recall medido sería otro).
        var rc = ObjectExtractor.Run(database, newInput, server);
        if (rc != 0) return rc;
        rc = TableSchemaExtractor.RunAll(database, newInput, server);
        if (rc != 0) return rc;

        var oracleRows = RunOracleQuery(server, database, File.ReadAllText(corpus.OracleQueryPath(repoRoot)));
        if (oracleRows == null) return ExitError;
        Utf8Io.WriteAllText(newOracle, string.Join("\n", oracleRows) + "\n");

        var drift = Compare(corpus, repoRoot, newInput, newOracle, oracleRows.Count);

        if (compatWarning != null)
        {
            Console.WriteLine();
            Console.WriteLine(compatWarning);
        }

        if (!write)
        {
            Console.WriteLine();
            Console.WriteLine(drift
                ? "DERIVA: la copia congelada no coincide con la base viva. Repite con --write si el cambio es intencionado."
                : "Sin deriva: la copia congelada coincide con la base viva.");
            Console.WriteLine($"(artefactos regenerados en {tmpDir}, el repo no se ha tocado)");
            return drift ? ExitDrift : ExitOk;
        }

        File.Copy(newInput, corpus.InputPath(repoRoot), overwrite: true);
        File.Copy(newOracle, corpus.OraclePath(repoRoot), overwrite: true);

        var updated = corpus with { Expected = new CorpusExpected(oracleRows.Count, corpus.Expected?.MinColumnEdges ?? 0) };
        manifest.Corpora[manifest.Corpora.FindIndex(c => c.Id == corpus.Id)] = updated;
        Utf8Io.WriteAllText(Path.Combine(repoRoot, "eval", CorpusManifest.FileName), manifest.Serialize());

        Console.WriteLine();
        Console.WriteLine($"Escritos {corpus.Input}, {corpus.Oracle} y expected.oracle_rows={oracleRows.Count}.");
        Console.WriteLine(
            "NO se han tocado los suelos (floors): son cifras MEDIDAS, no derivadas. Corre el gate de\n" +
            "recall, lee las cifras del informe y súbelas a mano si han mejorado.\n" +
            "Y hazlo en un commit SEPARADO de cualquier cambio del motor: si el corpus y el motor se\n" +
            "mueven a la vez, la cifra nueva no atribuye nada.");
        return ExitOk;
    }

    /// <summary>Compara lo regenerado contra lo congelado. Devuelve true si hay deriva.</summary>
    private static bool Compare(CorpusEntry corpus, string repoRoot, string newInput, string newOracle, int newOracleCount)
    {
        var drift = false;

        var oldNames = ObjectNames(corpus.InputPath(repoRoot));
        var newNames = ObjectNames(newInput);
        var addedObjs = newNames.Except(oldNames).OrderBy(x => x).ToList();
        var removedObjs = oldNames.Except(newNames).OrderBy(x => x).ToList();
        Console.WriteLine($"  input   congelado={oldNames.Count} entradas   base viva={newNames.Count}   (+{addedObjs.Count} / -{removedObjs.Count})");
        Report("nuevas en la base", addedObjs);
        Report("ya no en la base", removedObjs);
        drift |= addedObjs.Count > 0 || removedObjs.Count > 0;

        var oldRefs = File.Exists(corpus.OraclePath(repoRoot))
            ? File.ReadLines(corpus.OraclePath(repoRoot)).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var newRefs = File.ReadLines(newOracle).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        var addedRefs = newRefs.Except(oldRefs).OrderBy(x => x).ToList();
        var removedRefs = oldRefs.Except(newRefs).OrderBy(x => x).ToList();
        Console.WriteLine($"  oráculo congelado={oldRefs.Count} filas      base viva={newRefs.Count}   (+{addedRefs.Count} / -{removedRefs.Count})");
        Report("referencias nuevas", addedRefs);
        Report("referencias perdidas", removedRefs);
        drift |= addedRefs.Count > 0 || removedRefs.Count > 0;

        if (corpus.Expected != null && corpus.Expected.OracleRows != newOracleCount)
        {
            Console.WriteLine($"  expected.oracle_rows declara {corpus.Expected.OracleRows}, la base viva da {newOracleCount}");
            drift = true;
        }
        return drift;
    }

    private static void Report(string label, List<string> items)
    {
        if (items.Count == 0) return;
        Console.WriteLine($"      {label} ({items.Count}):");
        foreach (var x in items.Take(10))
            Console.WriteLine($"        {x}");
        if (items.Count > 10)
            Console.WriteLine($"        ... y {items.Count - 10} más");
    }

    /// <summary>
    /// Nombres de objeto de un input.json (<c>[{ "Name": "Db::schema.obj", "Sql": "..." }]</c>).
    ///
    /// La propiedad va en PascalCase porque la serializa <see cref="SourceObject"/> sin política
    /// de nombres, y <c>TryGetProperty</c> distingue mayúsculas: la primera versión de esto
    /// buscaba <c>"name"</c>, no encontraba ninguna, y el comparador anunciaba "congelado=0
    /// entradas, base viva=0, sin deriva" — un 0 de 0 presentado como aprobado, que es
    /// exactamente el defecto que este proyecto ya se pilló una vez en `lineage_coverage`.
    /// De ahí que un fichero que existe y no da un solo nombre sea un error duro y no un
    /// conjunto vacío: un comparador que no puede detectar nada no es un comparador.
    /// </summary>
    private static HashSet<string> ObjectNames(string inputPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(inputPath)) return set;
        using var doc = JsonDocument.Parse(File.ReadAllText(inputPath));
        foreach (var el in doc.RootElement.EnumerateArray())
            foreach (var prop in el.EnumerateObject())
                if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.GetString() is { } name)
                    set.Add(name);

        if (set.Count == 0)
            throw new InvalidOperationException(
                $"{inputPath} no aportó ni un nombre de objeto. O el fichero está vacío o su forma " +
                "cambió; en cualquier caso la comparación no mediría nada y diría que todo está bien.");
        return set;
    }

    /// <summary>
    /// Ejecuta el script de oráculo (un lote único con cursor y tabla temporal, sin GO) y devuelve
    /// su única columna de resultado, ordenada y sin duplicados — el mismo contenido que producía
    /// la receta de sqlcmd, pero sin depender de sqlcmd ni de filtrar su cabecera con findstr.
    /// </summary>
    private static List<string>? RunOracleQuery(string server, string database, string sql)
    {
        try
        {
            using var conn = new SqlConnection(
                SqlConnections.Build(server, database, 10, SqlConnections.FromEnvironment()));
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            using var reader = cmd.ExecuteReader();
            var rows = new SortedSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                if (!reader.IsDBNull(0))
                    rows.Add(reader.GetString(0));
            return rows.ToList();
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"Falló la consulta de oráculo contra {server}/{database}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// El nivel de compatibilidad cambia cómo resuelven las DMV, así que un oráculo extraído bajo
    /// otro nivel no es el mismo oráculo. No es motivo para fallar (la base puede haberse migrado
    /// a propósito), sí para decirlo en voz alta.
    /// </summary>
    private static string? CheckCompatibilityLevel(string server, string database, int declared)
    {
        try
        {
            using var conn = new SqlConnection(
                SqlConnections.Build(server, "master", 10, SqlConnections.FromEnvironment()));
            conn.Open();
            using var cmd = new SqlCommand("SELECT compatibility_level FROM sys.databases WHERE name = @db;", conn);
            cmd.Parameters.AddWithValue("@db", database);
            var actual = cmd.ExecuteScalar();
            if (actual is byte b && b != declared)
                return $"AVISO: compatibility_level declarado {declared}, el de la base es {b}. " +
                       "Las DMV pueden resolver distinto; el oráculo regenerado no es comparable al congelado.";
            return null;
        }
        catch (SqlException)
        {
            return null;   // no poder comprobarlo no es motivo para abortar la regeneración
        }
    }

    private static int CountLines(string path)
    {
        var n = 0;
        foreach (var l in File.ReadLines(path))
            if (l.Length > 0) n++;
        return n;
    }
}
