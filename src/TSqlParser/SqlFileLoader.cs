using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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
        var seenAt = new Dictionary<string, (string File, int EntryIndex)>(StringComparer.OrdinalIgnoreCase);

        // Two batches resolving to the same name is normal, not an error: the
        // idempotent-install pattern "IF OBJECT_ID(...) IS NULL EXECUTE (N'CREATE
        // PROCEDURE dbo.X AS RETURN 138;'); GO ALTER PROCEDURE dbo.X (...) AS ..."
        // (DarlingData's sp_HealthParser/sp_PressureDetector/etc.) legitimately
        // produces two batches under the real object's name: a throwaway one-line
        // stub and the real body, hundreds/thousands of lines later in the same
        // file. "First batch wins" would keep whichever happens to come first in
        // file order - the trivial stub, since the guard always precedes the real
        // definition - discarding the real body entirely. Keep the larger instead:
        // a placeholder stub is always tiny: real object bodies never are.
        void Add(string schema, string table, string body, string file)
        {
            var name = $"{database}::{schema}.{table}";
            if (seenAt.TryGetValue(name, out var existing))
            {
                if (body.Length <= entries[existing.EntryIndex].Sql.Length)
                {
                    Console.Error.WriteLine($"  ! duplicate object {name} (already defined in {existing.File}, {entries[existing.EntryIndex].Sql.Length} chars vs {body.Length}); keeping the larger");
                    return;
                }
                Console.Error.WriteLine($"  ! duplicate object {name} (already defined in {existing.File}, {entries[existing.EntryIndex].Sql.Length} chars); replacing with the larger definition from {file} ({body.Length} chars)");
                entries[existing.EntryIndex] = new SourceObject(name, body);
                seenAt[name] = (file, existing.EntryIndex);
                return;
            }
            seenAt[name] = (file, entries.Count);
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

    /// <summary>
    /// Last-resort fallback only: matches "CREATE [OR ALTER] PROC/FUNCTION/TRIGGER/
    /// VIEW/TABLE/SYNONYM &lt;name&gt;" as raw text. Used only when ScriptDom recovers
    /// no batch at all (see DetectObjectName) - e.g. a script broken badly enough that
    /// error recovery gives up on the whole thing, not just the offending statement.
    /// A text regex can't tell a real statement from the same words in a comment or
    /// string literal, so it must never be the primary path: that exact confusion
    /// silently misattributed DarlingData's real 6000-line sp_HealthParser body to a
    /// node named "dbo.failed" (from a comment reading "... CREATE TABLE failed.").
    /// Here the risk is bounded: a file ScriptDom can't recover at all is already
    /// going to be flagged as a parse error downstream, so a wrong name on it is far
    /// cheaper than one on an otherwise-healthy, substantial object.
    /// </summary>
    private static readonly Regex LastResortCreateRegex = new(
        @"CREATE\s+(?:OR\s+ALTER\s+)?(?:PROC(?:EDURE)?|FUNCTION|TRIGGER|VIEW|TABLE|SYNONYM)\s+(\[?[\w$]+\]?)(?:\s*\.\s*(\[?[\w$]+\]?))?",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Finds the schema/name of the first CREATE/ALTER/CREATE-OR-ALTER
    /// PROCEDURE/FUNCTION/TRIGGER/VIEW/TABLE/SYNONYM statement, primarily by parsing
    /// <paramref name="sql"/> with ScriptDom instead of regex-matching the raw text.
    ///
    /// A text regex for "CREATE ... PROC/TABLE/..." cannot tell a real top-level
    /// statement from the same words appearing inside a comment or a dynamic-SQL
    /// string literal: a real script hit this exactly (DarlingData's sp_HealthParser
    /// has "... and the resulting CREATE TABLE failed." in a comment), which made the
    /// object's real, 6000-line ALTER PROCEDURE body get filed under the bogus name
    /// "dbo.failed" - the whole procedure silently attributed to the wrong object.
    /// ScriptDom can't make that mistake: a comment or string literal never produces
    /// a CreateTableStatement/AlterProcedureStatement/etc. node, so this only matches
    /// a statement the batch actually executes. Errors elsewhere in the batch don't
    /// stop this: ScriptDom's error recovery keeps the outer CREATE/ALTER PROCEDURE
    /// even when its body doesn't parse (deliberately broken test fixtures rely on
    /// exactly this - the object should still get its real name, then fail to parse
    /// under it). Only when recovery finds no batch at all does this fall back to
    /// LastResortCreateRegex.
    /// </summary>
    private static (string schema, string name)? DetectObjectName(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> _);

        var finder = new ObjectNameFinder();
        fragment.Accept(finder);
        if (finder.Name is { } name)
        {
            var schema = name.SchemaIdentifier?.Value ?? "dbo";
            return (schema, name.BaseIdentifier.Value);
        }

        var match = LastResortCreateRegex.Match(sql);
        if (!match.Success)
            return null;

        var first = match.Groups[1].Value.Trim('[', ']');
        return match.Groups[2].Success
            ? (first, match.Groups[2].Value.Trim('[', ']'))
            : ("dbo", first);
    }

    /// <summary>
    /// Every CREATE/ALTER/CREATE OR ALTER variant of each object kind is its own
    /// concrete ScriptDom type (e.g. AlterProcedureStatement, CreateOrAlterViewStatement);
    /// TSqlFragmentVisitor dispatches by the node's exact compile-time type, not its
    /// base class, so all three variants must be listed for every kind (same pattern
    /// as TableAnalyzer.CreateTableFinder). Keeps the first one found; a batch is one
    /// object definition by construction (SqlFileLoader splits on GO first).
    /// </summary>
    private sealed class ObjectNameFinder : TSqlFragmentVisitor
    {
        public SchemaObjectName? Name { get; private set; }

        public override void Visit(CreateProcedureStatement node) => Name ??= node.ProcedureReference.Name;
        public override void Visit(AlterProcedureStatement node) => Name ??= node.ProcedureReference.Name;
        public override void Visit(CreateOrAlterProcedureStatement node) => Name ??= node.ProcedureReference.Name;

        public override void Visit(CreateFunctionStatement node) => Name ??= node.Name;
        public override void Visit(AlterFunctionStatement node) => Name ??= node.Name;
        public override void Visit(CreateOrAlterFunctionStatement node) => Name ??= node.Name;

        public override void Visit(CreateTriggerStatement node) => Name ??= node.Name;
        public override void Visit(AlterTriggerStatement node) => Name ??= node.Name;
        public override void Visit(CreateOrAlterTriggerStatement node) => Name ??= node.Name;

        public override void Visit(CreateViewStatement node) => Name ??= node.SchemaObjectName;
        public override void Visit(AlterViewStatement node) => Name ??= node.SchemaObjectName;
        public override void Visit(CreateOrAlterViewStatement node) => Name ??= node.SchemaObjectName;

        public override void Visit(CreateTableStatement node) => Name ??= node.SchemaObjectName;
        public override void Visit(CreateSynonymStatement node) => Name ??= node.Name;
    }
}
