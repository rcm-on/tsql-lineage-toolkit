using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// Connects to a live SQL Server database and dumps the SQL definitions of its
/// procedures, functions, triggers and views (sys.sql_modules) as the
/// `[{ "name": "Database::schema.object", "sql": "CREATE ..." }]` shape that
/// TSqlParser's main pipeline (Program.cs / SqlAnalyzer) expects as input.json.
/// </summary>
public static class ObjectExtractor
{
    /// <param name="objectNames">
    /// If non-empty, restricts extraction to these "schema.object" names
    /// (case-insensitive exact match). If empty, extracts every
    /// procedure/function/trigger/view in the database.
    /// </param>
    /// <param name="likePattern">
    /// If set, restricts extraction to objects whose "schema.object" name
    /// matches this T-SQL LIKE pattern (e.g. "dbo.usp_Sales%").
    /// </param>
    public static int Run(string database, string outputPath, string server, IReadOnlyCollection<string>? objectNames = null, string? likePattern = null)
    {
        using var conn = Connect(server, database);
        if (conn == null)
        {
            Console.Error.WriteLine($"Could not connect to {server}/{database}");
            return 1;
        }

        var query = @"
SELECT s.name AS schema_name, o.name AS object_name, m.definition AS sql_definition
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'TR', 'V')";
        if (likePattern != null)
            query += " AND (s.name + '.' + o.name) LIKE @like";
        query += " ORDER BY s.name, o.name;";

        var wanted = objectNames is { Count: > 0 }
            ? objectNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var entries = new List<SourceObject>();
        using var cmd = new SqlCommand(query, conn);
        if (likePattern != null)
            cmd.Parameters.AddWithValue("@like", likePattern);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var def = reader["sql_definition"];
            if (def is DBNull) continue;
            var schema = (string)reader["schema_name"];
            var name = (string)reader["object_name"];
            if (wanted != null && !wanted.Contains($"{schema}.{name}"))
                continue;
            entries.Add(new SourceObject($"{database}::{schema}.{name}", (string)def));
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(entries, jsonOptions));

        Console.WriteLine($"Wrote {entries.Count} objects from {database} to {outputPath}");
        return 0;
    }

    private static SqlConnection? Connect(string server, string database)
    {
        var connStr = $"Server={server};Database={database};Integrated Security=true;TrustServerCertificate=true;Connection Timeout=10;";
        try
        {
            var conn = new SqlConnection(connStr);
            conn.Open();
            return conn;
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"Connection failed: {ex.Message}");
            return null;
        }
    }
}
