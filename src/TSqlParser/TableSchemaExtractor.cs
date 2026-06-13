using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// .NET equivalent of extract_table_schemas.py: for every :Table node in a
/// previously exported graph (graph_full.json etc.), connects to the live
/// database and reconstructs its CREATE TABLE DDL (columns, types, identity,
/// nullability, PK, FKs) via sys.columns/sys.key_constraints/sys.foreign_keys,
/// then appends {"name": "<db>::<schema.table>", "sql": "CREATE TABLE ..."}
/// entries to input.json so TableAnalyzer/BuildTableSchemas can pick them up
/// on the next run. Skips tables already present in input.json, temp tables
/// (#...), table variables (@...), and anything without a "." in its name.
/// </summary>
public static class TableSchemaExtractor
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase) { "deleted", "inserted" };
    private static readonly HashSet<string> LenTypes = new(StringComparer.OrdinalIgnoreCase) { "varchar", "nvarchar", "char", "nchar", "varbinary", "binary" };
    private static readonly HashSet<string> PrecisionTypes = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };

    public static int Run(string graphPath, string inputPath, string server)
    {
        var byDb = CollectTableNames(graphPath);
        Console.WriteLine($"Found {byDb.Sum(kv => kv.Value.Count)} candidate tables across {byDb.Count} database(s)");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var existing = JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(inputPath), jsonOptions)
            ?? throw new InvalidDataException("Could not parse input JSON");
        var existingNames = existing.Select(e => e.Name).ToHashSet();

        var newEntries = new List<SourceObject>();
        foreach (var (db, tables) in byDb)
        {
            Console.WriteLine($"\n=== {db} ===");
            using var conn = Connect(server, db);
            if (conn == null)
            {
                Console.WriteLine("  Could not connect.");
                continue;
            }

            foreach (var tableRef in tables.OrderBy(t => t, StringComparer.Ordinal))
            {
                var (schema, table) = SplitSchemaTable(tableRef);
                var objName = $"{db}::{schema}.{table}";
                if (existingNames.Contains(objName))
                {
                    Console.WriteLine($"  - {tableRef}: already in input.json, skipping");
                    continue;
                }
                var ddl = BuildCreateTable(conn, schema, table);
                if (ddl == null)
                {
                    Console.WriteLine($"  - {tableRef}: not found (alias or wrong db?)");
                    continue;
                }
                newEntries.Add(new SourceObject(objName, ddl));
                Console.WriteLine($"  + {tableRef}: ok");
            }
        }

        if (newEntries.Count == 0)
        {
            Console.WriteLine("\nNo new table definitions to add.");
            return 0;
        }

        existing.AddRange(newEntries);
        File.WriteAllText(inputPath, JsonSerializer.Serialize(existing, jsonOptions), Encoding.UTF8);
        Console.WriteLine($"\nAppended {newEntries.Count} table definitions to {inputPath}");
        return 0;
    }

    private static Dictionary<string, HashSet<string>> CollectTableNames(string graphPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(graphPath));
        var byDb = new Dictionary<string, HashSet<string>>();

        foreach (var node in doc.RootElement.GetProperty("Nodes").EnumerateArray())
        {
            var labels = node.GetProperty("Labels").EnumerateArray().Select(l => l.GetString()).ToList();
            if (!labels.Contains("Table"))
                continue;

            var props = node.GetProperty("Properties");
            var db = props.TryGetProperty("database", out var dbEl) ? dbEl.GetString() ?? "" : "";
            var rawName = props.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var cleaned = CleanName(rawName);
            if (db.Length > 0 && cleaned != null)
                (byDb.TryGetValue(db, out var set) ? set : byDb[db] = new HashSet<string>()).Add(cleaned);
        }

        return byDb;
    }

    /// <summary>"[Schema].[Table]" -> "Schema.Table", or null if not a real persistent table reference.</summary>
    private static string? CleanName(string raw)
    {
        var name = raw.Trim('[', ']');
        var dotIdx = name.LastIndexOf('.');
        if (dotIdx < 0)
            return null;

        var schema = name[..dotIdx].Trim('[', ']');
        var table = name[(dotIdx + 1)..].Trim('[', ']');
        if (schema.StartsWith('#') || schema.StartsWith('@') || table.StartsWith('#') || table.StartsWith('@'))
            return null;
        if (string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) || SkipNames.Contains($"{schema}.{table}"))
            return null;

        return $"{schema}.{table}";
    }

    private static (string schema, string table) SplitSchemaTable(string schemaTable)
    {
        var idx = schemaTable.IndexOf('.');
        return (schemaTable[..idx], schemaTable[(idx + 1)..]);
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
        catch (SqlException)
        {
            return null;
        }
    }

    private record ColumnRow(string Name, string DataType, short MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsIdentity);
    private record FkRow(string ConstraintName, string ColumnName, string RefSchema, string RefTable, string RefColumn);

    private static string? BuildCreateTable(SqlConnection conn, string schema, string table)
    {
        var columns = new List<ColumnRow>();
        using (var cmd = new SqlCommand("""
            SELECT c.name, tp.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id = o.object_id
            JOIN sys.types tp  ON c.user_type_id = tp.user_type_id
            WHERE SCHEMA_NAME(o.schema_id) = @schema AND o.name = @table AND o.type = 'U'
            ORDER BY c.column_id
        """, conn))
        {
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add(new ColumnRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt16(2),
                    reader.GetByte(3), reader.GetByte(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        }

        if (columns.Count == 0)
            return null;

        var pkColumns = new List<string>();
        using (var cmd = new SqlCommand("""
            SELECT c.name
            FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.objects o ON o.object_id = kc.parent_object_id
            WHERE SCHEMA_NAME(o.schema_id) = @schema AND o.name = @table AND kc.type = 'PK'
            ORDER BY ic.key_ordinal
        """, conn))
        {
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                pkColumns.Add(reader.GetString(0));
        }

        var fks = new List<FkRow>();
        using (var cmd = new SqlCommand("""
            SELECT fk.name, c.name, SCHEMA_NAME(rt.schema_id), rt.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.objects o  ON o.object_id = fk.parent_object_id
            JOIN sys.columns c  ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE SCHEMA_NAME(o.schema_id) = @schema AND o.name = @table
            ORDER BY fk.name, fkc.constraint_column_id
        """, conn))
        {
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                fks.Add(new FkRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        var colLines = columns.Select(ColumnDdl).ToList();

        if (pkColumns.Count > 0)
            colLines.Add($"    CONSTRAINT [PK_{schema}_{table}] PRIMARY KEY ({string.Join(", ", pkColumns.Select(c => $"[{c}]"))})");

        foreach (var group in fks.GroupBy(f => f.ConstraintName))
        {
            var first = group.First();
            var cols = string.Join(", ", group.Select(f => $"[{f.ColumnName}]"));
            var refCols = string.Join(", ", group.Select(f => $"[{f.RefColumn}]"));
            colLines.Add($"    CONSTRAINT [{group.Key}] FOREIGN KEY ({cols}) REFERENCES [{first.RefSchema}].[{first.RefTable}] ({refCols})");
        }

        return $"CREATE TABLE [{schema}].[{table}]\n(\n{string.Join(",\n", colLines)}\n)";
    }

    private static string ColumnDdl(ColumnRow col)
    {
        var dtype = col.DataType;
        if (LenTypes.Contains(dtype))
        {
            var maxLen = col.MaxLength;
            if ((dtype.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) || dtype.Equals("nchar", StringComparison.OrdinalIgnoreCase)) && maxLen > 0)
                maxLen /= 2;
            dtype = $"{dtype}({(maxLen == -1 ? "MAX" : maxLen.ToString())})";
        }
        else if (PrecisionTypes.Contains(dtype))
        {
            dtype = $"{dtype}({col.Precision},{col.Scale})";
        }

        var parts = new List<string> { $"[{col.Name}]", dtype };
        if (col.IsIdentity)
            parts.Add("IDENTITY(1,1)");
        parts.Add(col.IsNullable ? "NULL" : "NOT NULL");
        return "    " + string.Join(" ", parts);
    }
}
