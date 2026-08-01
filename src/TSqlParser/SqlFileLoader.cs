using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSqlParser;

/// <summary>
/// Builds an input.json from one or more local .sql files (no database
/// connection needed). Each file should contain a single CREATE [OR ALTER]
/// PROCEDURE/FUNCTION/TRIGGER/VIEW/TABLE statement; the object's
/// "schema.name" is detected from that statement (defaulting to schema "dbo"
/// if not qualified), producing entries
/// `{ "name": "Database::schema.name", "sql": "<file contents>" }`.
/// </summary>
public static class SqlFileLoader
{
    private static readonly Regex CreateRegex = new(
        @"CREATE\s+(?:OR\s+ALTER\s+)?(?:PROC(?:EDURE)?|FUNCTION|TRIGGER|VIEW|TABLE|SYNONYM)\s+(\[?[\w$]+\]?)(?:\s*\.\s*(\[?[\w$]+\]?))?",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// sqlcmd batch separator: GO alone on its own line (optionally with a repeat
    /// count). The trailing [ \t\r]* is load-bearing: scripts are CRLF and in
    /// multiline mode "$" matches before the \n but NOT before the \r, so a plain
    /// "GO[ \t]*$" silently never matches a Windows-authored script.
    /// </summary>
    private static readonly Regex BatchSeparator = new(
        @"^[ \t]*GO[ \t]*\d*[ \t\r]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static int Run(string database, string outputPath, IReadOnlyList<string> sqlFilePaths)
    {
        var files = ExpandPaths(sqlFilePaths);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No .sql files matched.");
            return 1;
        }

        var entries = new List<SourceObject>();
        var seenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string schema, string table, string body, string file)
        {
            var name = $"{database}::{schema}.{table}";
            if (seenNames.TryGetValue(name, out var firstFile))
            {
                Console.Error.WriteLine($"  ! duplicate object {name} (already defined in {firstFile}); keeping the first");
                return;
            }
            seenNames[name] = file;
            entries.Add(new SourceObject(name, body));
        }

        foreach (var file in files)
        {
            var sql = File.ReadAllText(file);

            // A file may hold a whole scripted database (SSMS "Generate Scripts",
            // DNN/DotNetNuke .SqlDataProvider, Redgate output...). Split it on GO
            // batches and emit one object per CREATE batch. Files that yield a
            // single object keep the WHOLE file as their SQL, exactly as before,
            // so per-object corpora (their SET ANSI_NULLS/GO preamble included)
            // are byte-for-byte unaffected.
            var objectBatches = SplitIntoObjectBatches(sql);
            if (objectBatches.Count > 1)
            {
                foreach (var (schema, table, batch) in objectBatches)
                    Add(schema, table, batch, file);
                Console.WriteLine($"  + {file} -> {objectBatches.Count} objects (multi-object script)");
                continue;
            }

            var (fileSchema, fileTable) = DetectObjectName(sql) ?? ("dbo", Path.GetFileNameWithoutExtension(file));
            Add(fileSchema, fileTable, sql, file);
            Console.WriteLine($"  + {file} -> {database}::{fileSchema}.{fileTable}");
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Utf8Io.WriteAllText(outputPath, JsonSerializer.Serialize(entries, jsonOptions));

        Console.WriteLine($"Wrote {entries.Count} objects from {files.Count} file(s) to {outputPath}");
        return 0;
    }

    private static List<string> ExpandPaths(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (path.Contains('*') || path.Contains('?'))
            {
                var dir = Path.GetDirectoryName(path);
                var pattern = Path.GetFileName(path);
                result.AddRange(Directory.EnumerateFiles(string.IsNullOrEmpty(dir) ? "." : dir, pattern));
            }
            else if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path, "*.sql", SearchOption.AllDirectories));
            }
            else
            {
                result.Add(path);
            }
        }
        return result;
    }

    /// <summary>
    /// Splits a script on GO batch separators and returns the batches that define
    /// an object, with the object name detected from each batch. Batches without a
    /// CREATE (SET options, GRANTs, INSERT seed data, index DDL...) are dropped:
    /// they belong to no object and would otherwise be attributed to whichever
    /// CREATE happened to come first in the file.
    /// </summary>
    private static List<(string Schema, string Name, string Sql)> SplitIntoObjectBatches(string sql)
    {
        var result = new List<(string, string, string)>();
        foreach (var raw in BatchSeparator.Split(sql))
        {
            var batch = raw.Trim();
            if (batch.Length == 0)
                continue;
            if (DetectObjectName(batch) is not { } named)
                continue;
            result.Add((named.schema, named.name, batch));
        }
        return result;
    }

    private static (string schema, string name)? DetectObjectName(string sql)
    {
        var match = CreateRegex.Match(sql);
        if (!match.Success)
            return null;

        var first = match.Groups[1].Value.Trim('[', ']');
        if (match.Groups[2].Success)
            return (first, match.Groups[2].Value.Trim('[', ']'));

        return ("dbo", first);
    }
}
