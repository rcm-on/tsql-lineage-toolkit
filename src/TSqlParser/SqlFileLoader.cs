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

    public static int Run(string database, string outputPath, IReadOnlyList<string> sqlFilePaths)
    {
        var files = ExpandPaths(sqlFilePaths);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No .sql files matched.");
            return 1;
        }

        var entries = new List<SourceObject>();
        foreach (var file in files)
        {
            var sql = File.ReadAllText(file);
            var (schema, table) = DetectObjectName(sql) ?? ("dbo", Path.GetFileNameWithoutExtension(file));
            entries.Add(new SourceObject($"{database}::{schema}.{table}", sql));
            Console.WriteLine($"  + {file} -> {database}::{schema}.{table}");
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(entries, jsonOptions));

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
