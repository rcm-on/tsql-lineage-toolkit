using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSqlParser;

/// <summary>
/// Two-pass analysis of an input.json (the from-sql/extract output): CREATE TABLE
/// definitions first (so their column lists are available to expand "SELECT *"),
/// then procedures/functions/triggers. Extracted from Program.cs so tests and
/// other in-process consumers run exactly the same routing as the CLI pipeline.
/// </summary>
public static class InputAnalyzer
{
    private static readonly Regex CreateTableRegex = new(@"^\s*CREATE\s+TABLE\b", RegexOptions.IgnoreCase);

    public static (List<ObjectResult> Results, List<TableSchemaResult> TableSchemas) Analyze(string path)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sources = JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(path), opts)
            ?? throw new InvalidDataException("Could not parse input JSON");

        var schemas = new List<TableSchemaResult>();
        var objectSources = new List<SourceObject>();
        foreach (var src in sources)
        {
            // Route on the FIRST real statement, ignoring leading comments/whitespace:
            // a header comment before CREATE TABLE must not misroute the file to object
            // analysis (which would lose its PK and turn its reads/writes into TARGETS
            // edges instead of READS_FROM/WRITES_TO). The regex is a cheap fast path for
            // the common bare "CREATE TABLE ..." case; TableAnalyzer.LooksLikeTableScript
            // additionally catches the idempotent-install pattern "IF NOT EXISTS(...)
            // BEGIN CREATE TABLE ... END" (Ola Hallengren's CommandLog/Queue/QueueDatabase
            // all ship this way), which the regex alone would misroute to SqlAnalyzer -
            // and then to object_type=UNKNOWN, with the table never registered so an
            // unqualified reference elsewhere can't be normalized against it.
            if (CreateTableRegex.IsMatch(StripLeadingComments(src.Sql)) || TableAnalyzer.LooksLikeTableScript(src.Sql))
                schemas.Add(TableAnalyzer.AnalyzeTable(src.Name, src.Sql));
            else
                objectSources.Add(src);
        }

        var cols = new Dictionary<string, List<string>>();
        foreach (var schema in schemas)
        {
            if (schema.Error != null)
                continue;
            var parts = schema.ObjectName.Split("::", 2);
            if (parts.Length != 2)
                continue;
            cols[$"{parts[0]}::{SqlText.NormalizeRef(parts[1])}"] = schema.Columns.Select(c => c.Name).ToList();
        }

        var objResults = new List<ObjectResult>();
        foreach (var src in objectSources)
            objResults.Add(SqlAnalyzer.AnalyzeObject(src.Name, src.Sql, cols));

        return (objResults, schemas);
    }

    // Drops leading whitespace, "--" line comments and "/* */" block comments so the
    // CREATE-TABLE router sees the first real token. Returns "" if the input is only
    // comments/whitespace.
    public static string StripLeadingComments(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }

            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int nl = sql.IndexOf('\n', i + 2);
                if (nl < 0) return "";
                i = nl + 1;
                continue;
            }

            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return "";
                i = end + 2;
                continue;
            }

            break;
        }
        return i == 0 ? sql : sql.Substring(i);
    }
}
