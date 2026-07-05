using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>Renders ScriptDom AST fragments back to single-line SQL text for condition/target labels.</summary>
public static class SqlText
{
    public static string Generate(TSqlFragment? fragment)
    {
        if (fragment == null) return "";
        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
        });
        generator.GenerateScript(fragment, out string sql);
        return sql.Replace("\r\n", " ").Replace("\n", " ").Trim();
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Strips brackets/whitespace and lowercases so "[Schema].[Table]" matches "schema.table".</summary>
    public static string NormalizeRef(string raw) =>
        Regex.Replace(raw, @"[\[\]]", "").Trim().ToLowerInvariant();

    /// <summary>
    /// Strips brackets/whitespace but preserves casing - for display-facing name
    /// properties (e.g. a :Table node's "name") where NormalizeRef's lowercasing
    /// would be wrong. "[DBAtools].[dbo].[BlitzFirst]" -> "DBAtools.dbo.BlitzFirst".
    /// </summary>
    public static string StripBrackets(string raw) =>
        Regex.Replace(raw, @"[\[\]]", "").Trim();
}
