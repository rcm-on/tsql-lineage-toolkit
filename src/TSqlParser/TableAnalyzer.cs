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
        var finder = new CreateTableFinder();
        script.Accept(finder);
        var cts = finder.First;
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

                case UniqueConstraintDefinition { IsPrimaryKey: false } uq:
                    result.Constraints.Add(new ConstraintDef(
                        "UNIQUE",
                        string.Join(", ", uq.Columns.Select(col => LastIdentifier(col.Column.MultiPartIdentifier))),
                        uq.Columns.Select(col => LastIdentifier(col.Column.MultiPartIdentifier)).ToList(),
                        uq.ConstraintIdentifier?.Value));
                    break;

                case CheckConstraintDefinition chk:
                    result.Constraints.Add(new ConstraintDef(
                        "CHECK", SqlText.Generate(chk.CheckCondition),
                        CollectColumns(chk.CheckCondition), chk.ConstraintIdentifier?.Value));
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
                    case UniqueConstraintDefinition { IsPrimaryKey: false } uqc:
                        result.Constraints.Add(new ConstraintDef("UNIQUE", colName, new List<string> { colName }, uqc.ConstraintIdentifier?.Value));
                        break;
                    case CheckConstraintDefinition chkc:
                        result.Constraints.Add(new ConstraintDef("CHECK", SqlText.Generate(chkc.CheckCondition), CollectColumns(chkc.CheckCondition), chkc.ConstraintIdentifier?.Value));
                        break;
                    case DefaultConstraintDefinition dcd:
                        result.Constraints.Add(new ConstraintDef("DEFAULT", SqlText.Generate(dcd.Expression), new List<string> { colName }, dcd.ConstraintIdentifier?.Value));
                        break;
                }
            }

            // A column DEFAULT can also hang off the dedicated DefaultConstraint property
            // (not the Constraints list), depending on how the DDL was written.
            if (col.DefaultConstraint != null)
                result.Constraints.Add(new ConstraintDef("DEFAULT", SqlText.Generate(col.DefaultConstraint.Expression), new List<string> { colName }, col.DefaultConstraint.ConstraintIdentifier?.Value));

            var computedExpr = "";
            IReadOnlyList<string> computedSources = Array.Empty<string>();
            IReadOnlyList<string> computedOps = Array.Empty<string>();
            if (col.ComputedColumnExpression != null)
            {
                computedExpr = SqlText.Generate(col.ComputedColumnExpression);
                var collector = new ColumnRefCollector();
                col.ComputedColumnExpression.Accept(collector);
                computedSources = collector.Columns
                    .Where(c => !string.Equals(c, colName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                computedOps = OperatorClassifier.Classify(col.ComputedColumnExpression);
            }

            result.Columns.Add(new ColumnDef(colName, dataType, isNullable, col.IdentityOptions != null, isPk, ordinal, computedExpr, computedSources, computedOps));
        }

        return result;
    }

    /// <summary>
    /// True when this script's job is to (possibly conditionally - e.g. the common
    /// idempotent-install pattern "IF NOT EXISTS(...) BEGIN CREATE TABLE ... END",
    /// as seen throughout Ola Hallengren's maintenance solution) create one table, so
    /// InputAnalyzer's router can send it to AnalyzeTable instead of SqlAnalyzer.
    /// Unlike a literal "^CREATE TABLE" text match, this parses the whole script and
    /// walks every descendant fragment (TSqlFragmentVisitor recurses into IF/BEGIN...END
    /// bodies automatically), so a CREATE TABLE nested inside a guard is still found.
    /// False when the script also defines a procedure/function/trigger/view - those
    /// must go through the object pipeline (their reads/writes need TARGETS/
    /// READS_FROM/WRITES_TO edges, not a column schema) even on the rare chance they
    /// also create a real (non-temp) table inline.
    /// </summary>
    public static bool LooksLikeTableScript(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0)
            return false;

        var finder = new CreateTableFinder();
        fragment.Accept(finder);
        return finder.First != null && !finder.HasObjectDefinition;
    }

    /// <summary>
    /// Finds the first CREATE TABLE statement anywhere in a script - including one
    /// nested inside an IF guard or BEGIN...END block, not just a top-level batch
    /// statement - and flags whether the script also defines a procedure/function/
    /// trigger/view (which routes it away from table-schema analysis entirely).
    /// Every CREATE/ALTER/CREATE OR ALTER variant of each object kind is its own
    /// concrete ScriptDom type (e.g. AlterProcedureStatement, CreateOrAlterViewStatement)
    /// - overriding only the CREATE overload would miss the common Ola-Hallengren-style
    /// "IF NOT EXISTS(...) EXEC(@sql) END; ALTER PROCEDURE ..." install pattern (the
    /// literal CREATE never appears in the script; only ALTER does), which would then
    /// misroute the procedure's own inline "CREATE TABLE #Temp (...)" as if the whole
    /// file were a table schema. TSqlFragmentVisitor dispatches by the node's exact
    /// compile-time type, not its base class, so all three variants must be listed for
    /// every kind rather than relying on their common base (ProcedureStatementBody etc).
    /// </summary>
    private sealed class CreateTableFinder : TSqlFragmentVisitor
    {
        public CreateTableStatement? First { get; private set; }
        public bool HasObjectDefinition { get; private set; }

        public override void Visit(CreateTableStatement node) => First ??= node;

        public override void Visit(CreateProcedureStatement node) => HasObjectDefinition = true;
        public override void Visit(AlterProcedureStatement node) => HasObjectDefinition = true;
        public override void Visit(CreateOrAlterProcedureStatement node) => HasObjectDefinition = true;

        public override void Visit(CreateFunctionStatement node) => HasObjectDefinition = true;
        public override void Visit(AlterFunctionStatement node) => HasObjectDefinition = true;
        public override void Visit(CreateOrAlterFunctionStatement node) => HasObjectDefinition = true;

        public override void Visit(CreateTriggerStatement node) => HasObjectDefinition = true;
        public override void Visit(AlterTriggerStatement node) => HasObjectDefinition = true;
        public override void Visit(CreateOrAlterTriggerStatement node) => HasObjectDefinition = true;

        public override void Visit(CreateViewStatement node) => HasObjectDefinition = true;
        public override void Visit(AlterViewStatement node) => HasObjectDefinition = true;
        public override void Visit(CreateOrAlterViewStatement node) => HasObjectDefinition = true;
    }

    private static string LastIdentifier(MultiPartIdentifier ident) =>
        ident.Identifiers.Count > 0 ? ident.Identifiers[^1].Value : "";

    /// <summary>Distinct (unqualified) column names referenced anywhere in a fragment - e.g. a CHECK predicate "Qty > 0 AND Price >= 0" -> ["Qty", "Price"].</summary>
    private static List<string> CollectColumns(TSqlFragment fragment)
    {
        var collector = new ColumnRefCollector();
        fragment.Accept(collector);
        return collector.Columns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Collects the (unqualified) column names referenced by a computed-column expression - e.g. "Price * Qty" -> ["Price", "Qty"].</summary>
    private sealed class ColumnRefCollector : TSqlFragmentVisitor
    {
        public List<string> Columns { get; } = new();

        public override void Visit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is { Count: > 0 })
                Columns.Add(ids[^1].Value);
        }
    }
}
