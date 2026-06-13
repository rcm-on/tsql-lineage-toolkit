using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>
/// Entry point for analyzing one CREATE TABLE definition into its column list
/// (name, data type, nullability, identity, primary key) and foreign keys.
/// Unlike SqlAnalyzer (procedures/functions/triggers), this reads the table's
/// own DDL rather than its usage - it's the source of truth for column types
/// and FKs, which AstWalker can never know just from INSERT/UPDATE/SELECT text.
/// </summary>
public static class TableAnalyzer
{
    public static TableSchemaResult AnalyzeTable(string name, string? sql)
    {
        var result = new TableSchemaResult(name);

        if (string.IsNullOrWhiteSpace(sql))
        {
            result.Error = "empty definition";
            return result;
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
        {
            result.Error = string.Join("; ", errors.Select(e => $"L{e.Line}: {e.Message}"));
            return result;
        }

        var script = (TSqlScript)fragment;
        var cts = script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().FirstOrDefault();
        if (cts?.Definition == null)
        {
            result.Error = "not a CREATE TABLE statement";
            return result;
        }

        // Table-level constraints: PRIMARY KEY (Col1, Col2, ...) and FOREIGN KEY (...) REFERENCES ...
        var pkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cts.Definition.TableConstraints)
        {
            switch (c)
            {
                case UniqueConstraintDefinition { IsPrimaryKey: true } uc:
                    foreach (var col in uc.Columns)
                        pkColumns.Add(LastIdentifier(col.Column.MultiPartIdentifier));
                    break;

                case ForeignKeyConstraintDefinition fk:
                    result.ForeignKeys.Add(new ForeignKeyDef(
                        fk.Columns.Select(id => id.Value).ToList(),
                        SqlText.Generate(fk.ReferenceTableName),
                        fk.ReferencedTableColumns.Select(id => id.Value).ToList(),
                        fk.ConstraintIdentifier?.Value
                    ));
                    break;
            }
        }

        var ordinal = 0;
        foreach (var col in cts.Definition.ColumnDefinitions)
        {
            ordinal++;
            var colName = col.ColumnIdentifier.Value;
            var dataType = SqlText.Generate(col.DataType);
            var isNullable = true;
            var isPk = pkColumns.Contains(colName);

            // Inline column constraints: NULL/NOT NULL, PRIMARY KEY, REFERENCES ...
            foreach (var con in col.Constraints)
            {
                switch (con)
                {
                    case NullableConstraintDefinition ncd:
                        isNullable = ncd.Nullable;
                        break;
                    case UniqueConstraintDefinition { IsPrimaryKey: true }:
                        isPk = true;
                        break;
                    case ForeignKeyConstraintDefinition fkc:
                        result.ForeignKeys.Add(new ForeignKeyDef(
                            new List<string> { colName },
                            SqlText.Generate(fkc.ReferenceTableName),
                            fkc.ReferencedTableColumns.Select(id => id.Value).ToList(),
                            fkc.ConstraintIdentifier?.Value
                        ));
                        break;
                }
            }

            result.Columns.Add(new ColumnDef(colName, dataType, isNullable, col.IdentityOptions != null, isPk, ordinal));
        }

        return result;
    }

    private static string LastIdentifier(MultiPartIdentifier ident) =>
        ident.Identifiers.Count > 0 ? ident.Identifiers[^1].Value : "";
}
