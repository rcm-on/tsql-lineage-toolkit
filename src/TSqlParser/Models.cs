// Data model shared between the AST walker and the JSON output.
// Mirrors the shape produced by Python's workflow_analyzer.ObjectWorkflow,
// so results from both engines can be compared object-by-object.

namespace TSqlParser;

/// <summary>One input row: a fully-qualified object name plus its CREATE ... definition.</summary>
public record SourceObject(string Name, string Sql);

/// <summary>One entry on the condition stack while walking the statement tree.</summary>
/// <param name="BlockId">
/// Source line of the control statement that opened this block (IF/WHILE/TRY).
/// Distinguishes two sibling blocks with identical condition text (e.g. two
/// separate "WHILE @@FETCH_STATUS = 0" cursor loops) so downstream consumers
/// don't collapse them into one. 0 when unknown.
/// </param>
public record Condition(string Type, string Text, int Depth, int BlockId = 0);

public record VariableInfo(string Name, string Type, string Default);

/// <summary>One causal link: condition (IF/WHILE/CATCH/...) -> consequence (INSERT/UPDATE/EXEC/...).</summary>
/// <param name="DynamicSqlVars">
/// For ConsequenceType == "EXEC" with target "(dynamic SQL)": the @variables that feed
/// the executed string (e.g. EXECUTE (@SQL) -> ["@SQL"]). Empty for everything else.
/// </param>
/// <param name="ConditionPath">
/// The full chain of enclosing conditions, outermost first, each rendered as
/// "TYPE: text" (e.g. ["IF: @a = 1", "IF: @b = 1", "WHILE: @c > 0", "IF: @a + @b = 2"]).
/// ConditionType/ConditionText above are just ConditionPath[^1] (the immediate
/// parent) - this is the full "sub-steps" trail down to this step.
/// </param>
/// <param name="Columns">
/// Columns of ConsequenceTarget touched by this step: target columns for
/// INSERT/UPDATE (what gets written), columns referenced in the SELECT list
/// for SELECT (what gets read). Empty when the columns couldn't be determined
/// (e.g. "INSERT INTO T SELECT * ...", or DELETE/MERGE/EXEC).
/// </param>
/// <param name="ColumnLineage">
/// For "INSERT INTO T (...) SELECT ... FROM S ...": one entry per target column,
/// pairing it with the source table S and the column(s) of S referenced by the
/// corresponding SELECT expression (more than one for e.g. "Col1 + Col2", none
/// for literals/functions with no column refs). Empty when the source can't be
/// determined (no FROM clause, UNION, target/source column-count mismatch, etc.).
/// </param>
/// <param name="UsedVariables">
/// The distinct @variables referenced anywhere in this statement (WHERE/SET/VALUES/
/// predicates, etc.), in source order - lets a Step be linked to every Variable whose
/// current value influences it, regardless of ConsequenceType.
/// </param>
/// <param name="ExtraReads">
/// For "SELECT ... FROM A [JOIN B ...]": JOIN partner tables beyond the primary
/// ConsequenceTarget (A), each paired with the columns of that table referenced
/// in the SELECT list (via an "alias.Column"/"table.Column" qualifier). Empty for
/// single-table FROMs or when a JOIN partner's columns can't be resolved.
/// </param>
/// <param name="FilterColumns">
/// Columns referenced in this step's WHERE clause and JOIN predicates (not the
/// SELECT/SET list), grouped by table the same way as ExtraReads. Lets a
/// consumer distinguish "columns written/selected" from "columns used to decide
/// which rows are touched". Empty when there's no WHERE/JOIN condition, or none
/// of its columns could be attributed to a known table.
/// </param>
/// <param name="FilterText">
/// Raw text of this step's own WHERE clause (WHERE only - not the JOIN ON
/// predicates also folded into FilterColumns), truncated like ConditionText.
/// Empty when the statement has no WhereClause (INSERT, MERGE's ON, a step with
/// only a JOIN condition, etc.) - GraphExporter uses emptiness here, not
/// FilterColumns emptiness, to decide whether a WHERE-derived :BusinessRule node
/// is warranted, so a plain JOIN with no WHERE never manufactures a rule out of
/// its key-matching ON clause.
/// </param>
/// <param name="FilterKind">
/// <see cref="FilterRuleClassifier"/>'s verdict on FilterText: "domain_filter"
/// (compares a column to a literal/state - business logic), "key_lookup"
/// (compares to a parameter/system value - row addressing, not a rule), or
/// "mixed"/"" when both shapes appear or none could be classified. Never used to
/// drop data - always a property alongside the full FilterText/FilterColumns, so
/// a consumer can choose to ignore key_lookup rules without the data having been
/// discarded upstream.
/// </param>
public record FlowLinkInfo(
    string ConditionType, string ConditionText,
    string ConsequenceType, string ConsequenceTarget,
    int NestingLevel, int LineNo,
    IReadOnlyList<string>? DynamicSqlVars = null,
    IReadOnlyList<string>? ConditionPath = null,
    IReadOnlyList<string>? ConditionKeys = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<ColumnDerivation>? ColumnLineage = null,
    IReadOnlyList<string>? UsedVariables = null,
    IReadOnlyList<TableColumnRef>? ExtraReads = null,
    IReadOnlyList<TableColumnRef>? FilterColumns = null,
    IReadOnlyList<string>? FilterOpKinds = null,
    string Detail = "",
    string DynamicSqlText = "",
    bool SelectStar = false,
    string FilterText = "",
    string FilterKind = "")
{
    public IReadOnlyList<string> DynamicSqlVars { get; init; } = DynamicSqlVars ?? Array.Empty<string>();
    public IReadOnlyList<string> ConditionPath { get; init; } = ConditionPath ?? Array.Empty<string>();
    /// <summary>Parallel to ConditionPath: "TYPE#BlockId" per entry, uniquely identifying each enclosing block instance.</summary>
    public IReadOnlyList<string> ConditionKeys { get; init; } = ConditionKeys ?? Array.Empty<string>();
    public IReadOnlyList<string> Columns { get; init; } = Columns ?? Array.Empty<string>();
    public IReadOnlyList<ColumnDerivation> ColumnLineage { get; init; } = ColumnLineage ?? Array.Empty<ColumnDerivation>();
    public IReadOnlyList<string> UsedVariables { get; init; } = UsedVariables ?? Array.Empty<string>();
    public IReadOnlyList<TableColumnRef> ExtraReads { get; init; } = ExtraReads ?? Array.Empty<TableColumnRef>();
    public IReadOnlyList<TableColumnRef> FilterColumns { get; init; } = FilterColumns ?? Array.Empty<TableColumnRef>();
    /// <summary>Normalized operator tokens of this step's WHERE/JOIN-ON predicates (see <see cref="OperatorClassifier"/>) - the AND/OR/comparison structure behind FilterColumns. Empty when there's no predicate.</summary>
    public IReadOnlyList<string> FilterOpKinds { get; init; } = FilterOpKinds ?? Array.Empty<string>();
    /// <summary>Short subtype label for ALTER steps (e.g. "DROP PERIOD", "ADD CONSTRAINT"). Empty for other action types.</summary>
    public string Detail { get; init; } = Detail;
    /// <summary>
    /// For "EXEC (dynamic SQL)" steps whose executed string could be reconstructed to a
    /// pure literal: the SQL that runs, whitespace-collapsed, USE-stripped and FULL
    /// (untruncated). Empty when the string is built at runtime and thus not statically
    /// resolvable. Consumed two ways: SqlAnalyzer.ResolveDynamicSqlLinks re-parses it for the
    /// inner DML's lineage, and GraphExporter emits a truncated copy as the descriptive
    /// "dynamic_sql" node property. Keep it full here so re-parse isn't capped at the display
    /// length.
    /// </summary>
    public string DynamicSqlText { get; init; } = DynamicSqlText;
}

/// <summary>
/// One target column of an INSERT...SELECT, paired with the source table/columns its
/// value comes from, the SQL expression (e.g. "s.Col1 + s.Col2") that computed it, and
/// the normalized operator tokens of that expression (see <see cref="OperatorClassifier"/>).
/// </summary>
public record ColumnDerivation(
    string TargetColumn, string SourceTable, IReadOnlyList<string> SourceColumns,
    string TransformationExpression = "", IReadOnlyList<string>? OpKinds = null)
{
    public IReadOnlyList<string> OpKinds { get; init; } = OpKinds ?? Array.Empty<string>();
}

/// <summary>One table referenced in a FROM/JOIN, paired with the columns of it that a step reads.</summary>
public record TableColumnRef(string Table, IReadOnlyList<string> Columns);

/// <summary>
/// A "CREATE TRIGGER" found in an object's body - typically inside resolved dynamic SQL
/// (EXEC of a built-up string), but also a literal one. Records enough to model the trigger
/// as its own node: its name, the table it fires ON, its timing (AFTER/INSTEAD OF/FOR) and
/// the DML events (INSERT/UPDATE/DELETE). The trigger BODY's own lineage is a later phase;
/// this is the "what trigger, on whom, when" layer (see docs/dynamic-trigger-modeling-spec.md).
/// </summary>
public record TriggerCreationInfo(
    string TriggerName, string OnTable, string Timing,
    IReadOnlyList<string> Events, int LineNo)
{
    public IReadOnlyList<string> Events { get; init; } = Events ?? Array.Empty<string>();
}

/// <summary>
/// One "SET @var = expr" / "SELECT @var = expr FROM S ..." assignment whose value
/// was traced back to column(s) of a real table S - column-to-variable lineage,
/// the mirror of ColumnDerivation for variables instead of target columns.
/// </summary>
public record VariableAssignmentInfo(string VariableName, string SourceTable, IReadOnlyList<string> SourceColumns);

public record ParamInfo(string Name, string DataType, bool IsOutput);

/// <summary>Mutable accumulator used while walking a single object's statement tree.</summary>
public class WalkContext
{
    public List<VariableInfo> Variables { get; } = new();
    public List<FlowLinkInfo> FlowLinks { get; } = new();
    public List<string> ExecCalls { get; } = new();
    public List<VariableAssignmentInfo> VariableAssignments { get; } = new();

    /// <summary>
    /// Per-variable list of the textual right-hand sides assigned to it
    /// ("'CREATE INDEX ' + @Name", "@SQL + ' ON ...'", ...) in source order - this
    /// reconstructs how a dynamic-SQL string was built. Keyed by @variable name.
    /// </summary>
    public Dictionary<string, List<string>> VariableConstructions { get; } = new();

    /// <summary>
    /// Per-variable union of the operator tokens (see <see cref="OperatorClassifier"/>)
    /// across every expression assigned to it - so a variable fed by "@a + @b" or a
    /// dynamic-SQL string built by "'... ' + @name" carries its arithmetic/concat
    /// operators, the same way a column does. Keyed by @variable name.
    /// </summary>
    public Dictionary<string, SortedSet<string>> VariableOpKinds { get; } = new();

    /// <summary>
    /// Per-variable current value, but only while it remains a pure string literal
    /// (concatenation of literals, possibly through other literal variables). Updated on
    /// each assignment in source order; an assignment whose RHS isn't fully literal
    /// (column ref, @param, function call, SELECT) removes the variable from the map.
    /// Lets a dynamic EXEC snapshot exactly what literal SQL it runs at that point.
    /// </summary>
    public Dictionary<string, string> ResolvedVars { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PROTOTYPE (dynsql-placeholder): @parameter names substituted via a placeholder
    /// identifier token anywhere in this object's body (see AstWalker.ResolveLiteral's
    /// QUOTENAME case) - accumulates across the whole walk, not cleared per-statement.
    /// Not itself consumed downstream: the placeholder token travels embedded in the
    /// reconstructed SQL text (ResolvedVars / DynamicSqlText) and is detected there by
    /// GraphExporter when it builds the edge. Kept on the context for diagnostics/testability.
    /// </summary>
    public HashSet<string> DynamicSqlPlaceholderParams { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SawTryCatch;
    public bool HasTransaction;
    public bool HasCursor;
    public int DecisionCount;
    public int DynamicSqlCount;

    /// <summary>Database part of the object being walked (e.g. "AdventureWorks2019"), used to look up TableColumns.</summary>
    public string Db { get; init; } = "";

    /// <summary>
    /// When the object being walked is a DML TRIGGER, the real table it fires ON
    /// (e.g. "Person.Person"). Inside a trigger body the pseudo-tables
    /// <c>inserted</c>/<c>deleted</c> are virtual row sets of this table, so the
    /// walker seeds them as pseudo-CTEs resolving here - reads/writes and column
    /// lineage land on the ON table and no phantom "inserted"/"deleted" :Table node
    /// is ever emitted (mirrors the MERGE ... OUTPUT INTO handling). Null/empty for
    /// every non-trigger object, so their behavior is unchanged.
    /// </summary>
    public string? TriggerOnTable { get; init; }

    /// <summary>
    /// Optional "{Database}::{schema.table}" (normalized, lowercase) -> column names,
    /// built from CREATE TABLE definitions (TableAnalyzer). Used to expand "SELECT *"
    /// and column-list-less INSERTs into their real column lists.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>>? TableColumns { get; init; }

    /// <summary>
    /// Same key shape as TableColumns ("{Database}::{schema.table}", normalized), but for
    /// tables discovered while walking this object's own body: table variables
    /// ("DECLARE @T TABLE (...)") and local temp tables ("CREATE TABLE #T (...)").
    /// Lets a later "SELECT * FROM @T"/"INSERT INTO #T" in the same body resolve its
    /// column list even though the table never existed in the database schema.
    /// Checked before the static TableColumns map (see TryGetColumns).
    /// </summary>
    public Dictionary<string, List<string>> TransientTableColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a column list, preferring a transient table/table-variable discovered in this body over the static schema map.</summary>
    public bool TryGetColumns(string key, out List<string>? columns)
    {
        if (TransientTableColumns.TryGetValue(key, out columns))
            return true;

        if (TableColumns != null && TableColumns.TryGetValue(key, out var baseCols))
        {
            columns = baseCols;
            return true;
        }

        columns = null;
        return false;
    }

    /// <summary>Registers the column list of a table variable or local temp table discovered while walking this body, keyed the same way as TableColumns.</summary>
    public void RegisterTransientTable(string tableName, List<string> columns)
    {
        if (string.IsNullOrEmpty(tableName)) return;
        var key = $"{Db}::{SqlText.NormalizeRef(tableName)}";
        TransientTableColumns[key] = columns;
    }
}

/// <summary>
/// One column of a CREATE TABLE, as declared in its DDL. ComputedExpression/
/// ComputedSourceColumns are non-empty only for computed columns ("AS Price * Qty"):
/// the SQL expression and the other column(s) of the same table it reads, so
/// GraphExporter can link the computed column to them via DERIVES_FROM.
/// </summary>
public record ColumnDef(
    string Name, string DataType, bool IsNullable, bool IsIdentity, bool IsPrimaryKey, int Ordinal,
    string ComputedExpression = "", IReadOnlyList<string>? ComputedSourceColumns = null,
    IReadOnlyList<string>? ComputedOpKinds = null)
{
    public IReadOnlyList<string> ComputedSourceColumns { get; init; } = ComputedSourceColumns ?? Array.Empty<string>();
    /// <summary>Normalized operator tokens of the computed-column expression (see <see cref="OperatorClassifier"/>); empty for non-computed columns.</summary>
    public IReadOnlyList<string> ComputedOpKinds { get; init; } = ComputedOpKinds ?? Array.Empty<string>();
}

/// <summary>
/// One FOREIGN KEY constraint: Columns (this table) -> ReferencedTable.ReferencedColumns,
/// paired up by position (Columns[i] references ReferencedColumns[i]).
/// </summary>
public record ForeignKeyDef(IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, string? ConstraintName = null);

/// <summary>
/// One declarative DDL constraint that carries business meaning beyond a column's own
/// attributes: a CHECK predicate, a DEFAULT expression, or a UNIQUE key. PK / FK / NOT
/// NULL are already represented (column attributes + FK edges), so they're not duplicated
/// here. Columns = the column(s) the constraint governs. Surfaced as :BusinessRule nodes
/// (HAS_RULE from the table, CONSTRAINS to each column).
/// </summary>
public record ConstraintDef(string Kind, string Expression, IReadOnlyList<string> Columns, string? Name = null);

/// <summary>Result of parsing one CREATE TABLE statement: its columns, foreign keys and declarative constraints.</summary>
public class TableSchemaResult
{
    public string ObjectName { get; }
    public List<ColumnDef> Columns { get; } = new();
    public List<ForeignKeyDef> ForeignKeys { get; } = new();
    public List<ConstraintDef> Constraints { get; } = new();
    public string? Error { get; set; }

    public TableSchemaResult(string objectName) => ObjectName = objectName;
}

/// <summary>Final per-object result, serialized to the output JSON.</summary>
public class ObjectResult
{
    public string ObjectName { get; }
    public List<ParamInfo> Parameters { get; } = new();
    public List<VariableInfo> Variables { get; } = new();
    public List<FlowLinkInfo> FlowLinks { get; } = new();
    public List<string> ExecCalls { get; } = new();
    public List<string> FunctionCalls { get; } = new();
    public List<VariableAssignmentInfo> VariableAssignments { get; } = new();
    public Dictionary<string, List<string>> VariableConstructions { get; } = new();
    /// <summary>Per-variable operator tokens (see <see cref="OperatorClassifier"/>), unioned across all its assignments. Surfaced on the Variable node as op_kinds.</summary>
    public Dictionary<string, List<string>> VariableOpKinds { get; } = new();
    /// <summary>For a VIEW: each output column paired with the base table column(s) its value comes from (see <see cref="OperatorClassifier"/> for ops). Empty for non-views. Surfaced as DERIVES_FROM off the view's own :Column nodes.</summary>
    public List<ColumnDerivation> ViewColumnLineage { get; } = new();
    /// <summary>For a TABLE_VALUED_FUNCTION/INLINE_TABLE_FUNCTION: its output column names (from RETURNS TABLE's SELECT list, or the declared RETURNS @t TABLE(...) columns). Empty otherwise. Lets InputAnalyzer register the function in the table-columns catalog so callers can expand "SELECT * FROM func()".</summary>
    public List<string> TvfOutputColumns { get; } = new();
    /// <summary>Triggers this object's body CREATEs (usually via resolved dynamic SQL). Surfaced as a :Trigger node with CREATES/ON edges. See <see cref="TriggerCreationInfo"/>.</summary>
    public List<TriggerCreationInfo> CreatedTriggers { get; } = new();
    public bool HasTransaction { get; set; }
    public bool HasErrorHandling { get; set; }
    public bool HasCursor { get; set; }
    public int DynamicSqlCount { get; set; }
    public int ComplexityScore { get; set; } = 1;
    public string? Error { get; set; }

    /// <summary>Object kind detected from the SQL definition: PROCEDURE, SCALAR_FUNCTION, TABLE_VALUED_FUNCTION, TRIGGER, VIEW, SYNONYM, or SCRIPT (bare DML batch).</summary>
    public string ObjectType { get; set; } = "UNKNOWN";

    /// <summary>For a CREATE SYNONYM object: the base object it points to (schema.name), used to
    /// resolve references to the synonym back onto the real table/view so impact analysis is not
    /// split between the alias and its target. Empty for non-synonyms.</summary>
    public string SynonymTarget { get; set; } = "";

    public ObjectResult(string objectName) => ObjectName = objectName;
}
