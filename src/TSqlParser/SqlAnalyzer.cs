using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>
/// Entry point for analyzing one CREATE PROCEDURE / FUNCTION / TRIGGER definition
/// into the same shape as Python's WorkflowAnalyzer.analyze_object: parameters,
/// variables, flow_links (rule -> step), exec_calls (caller -> callee) and the
/// usual transaction/cursor/error-handling/complexity flags.
/// </summary>
public static class SqlAnalyzer
{
    /// <param name="tableColumns">
    /// Optional "{Database}::{schema.table}" (normalized, lowercase) -> column names,
    /// from CREATE TABLE definitions. Lets AstWalker expand "SELECT *" and
    /// column-list-less INSERTs into real column lists.
    /// </param>
    public static ObjectResult AnalyzeObject(string name, string? sql, IReadOnlyDictionary<string, List<string>>? tableColumns = null)
    {
        var result = new ObjectResult(name);

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
        var topStatements = script.Batches.SelectMany(b => b.Statements).ToList();

        // CREATE SYNONYM has no body, so the general walk below returns early without
        // ever setting ObjectType. Capture it (and its target) here so references to the
        // synonym can be resolved back onto the real base object during graph build.
        var synonym = topStatements.OfType<CreateSynonymStatement>().FirstOrDefault();
        if (synonym != null)
        {
            result.ObjectType = "SYNONYM";
            result.SynonymTarget = SqlText.Generate(synonym.ForName);
        }

        result.Parameters.AddRange(FindParameters(topStatements));

        var dbParts = name.Split("::", 2);
        var db = dbParts.Length == 2 ? dbParts[0] : "";
        var ctx = new WalkContext { Db = db, TableColumns = tableColumns };

        var statementList = FindBodyStatementList(topStatements);

        // CREATE/ALTER VIEW has no StatementList (its body is a single SELECT
        // exposed via a "SelectStatement" property, not a nested statement block) -
        // walk that SELECT directly so the view's own lineage (which table/columns
        // it reads) gets computed exactly like any other object's SELECT step.
        var viewSelect = statementList == null ? FindViewSelect(topStatements) : null;
        var tvfSelect = statementList == null && viewSelect == null
            ? FindInlineTableFunctionSelect(topStatements) : null;

        IList<TSqlStatement> bodyStatements;
        if (statementList != null)
        {
            bodyStatements = statementList.Statements;
        }
        else if (viewSelect != null)
        {
            bodyStatements = new List<TSqlStatement> { viewSelect };
        }
        else if (tvfSelect != null)
        {
            bodyStatements = new List<TSqlStatement> { tvfSelect };
        }
        else
        {
            // Bare DML batch (INSERT/UPDATE/DELETE/SELECT/MERGE not inside a CREATE PROC body).
            // Walk the top-level statements directly so table targets are captured in lineage.
            var dml = topStatements.Where(s =>
                s is InsertStatement or UpdateStatement or DeleteStatement or
                SelectStatement or MergeStatement or ExecuteStatement).ToList();
            if (dml.Count == 0)
                return result; // e.g. inline scalar function: RETURN <expr> with no body
            bodyStatements = dml;
        }

        AstWalker.Walk(bodyStatements, new List<Condition>(), ctx, depth: 0);

        result.Variables.AddRange(ctx.Variables);
        result.FlowLinks.AddRange(ctx.FlowLinks);
        result.ExecCalls.AddRange(ctx.ExecCalls.Distinct());
        result.VariableAssignments.AddRange(ctx.VariableAssignments);
        // View output-column lineage: each "SELECT expr AS Out" column DERIVES_FROM its
        // base table column(s), making the view a real lineage hop (not just a reader).
        if (viewSelect != null)
            result.ViewColumnLineage.AddRange(AstWalker.ViewColumnLineage(viewSelect, FindViewColumns(topStatements)));
        foreach (var kv in ctx.VariableConstructions)
            result.VariableConstructions[kv.Key] = kv.Value;
        foreach (var kv in ctx.VariableOpKinds)
            result.VariableOpKinds[kv.Key] = kv.Value.ToList();

        var funcCollector = new AstWalker.FunctionCallCollector();
        if (statementList != null)
            statementList.Accept(funcCollector);
        else
            foreach (var s in bodyStatements)
                s.Accept(funcCollector);
        result.FunctionCalls.AddRange(funcCollector.Names);

        result.HasTransaction = ctx.HasTransaction;
        result.HasErrorHandling = ctx.SawTryCatch;
        result.HasCursor = ctx.HasCursor;
        result.DynamicSqlCount = ctx.DynamicSqlCount;
        result.ComplexityScore = 1 + ctx.DecisionCount;
        result.ObjectType = statementList != null ? DetectObjectType(topStatements)
            : viewSelect != null ? "VIEW"
            : tvfSelect != null ? "INLINE_TABLE_FUNCTION"
            : "SCRIPT";

        // Re-parse any EXEC steps whose dynamic SQL resolved to a pure literal:
        // extract INSERT/SELECT/UPDATE/DELETE/MERGE targets from the literal text
        // and inject them as additional FlowLinks so downstream lineage sees the
        // real tables the dynamic SQL touches (not just "(dynamic SQL)").
        ResolveDynamicSqlLinks(result, tableColumns);

        return result;
    }

    /// <summary>
    /// For each EXEC step in result.FlowLinks whose DynamicSqlText is a non-empty
    /// resolved literal, parses that literal SQL and walks its DML statements to
    /// extract table-level lineage (INSERT/SELECT/UPDATE/DELETE targets). The
    /// resulting FlowLinks are injected immediately after the EXEC step, inheriting
    /// its condition context, so downstream consumers see the real tables touched
    /// instead of just "(dynamic SQL)".
    /// </summary>
    private static void ResolveDynamicSqlLinks(ObjectResult result, IReadOnlyDictionary<string, List<string>>? tableColumns)
    {
        var dbParts = result.ObjectName.Split("::", 2);
        var db = dbParts.Length == 2 ? dbParts[0] : "";
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var toInsert = new List<(int afterIndex, List<FlowLinkInfo> links)>();

        for (int i = 0; i < result.FlowLinks.Count; i++)
        {
            var fl = result.FlowLinks[i];
            if (fl.ConsequenceType != "EXEC" || fl.DynamicSqlText.Length == 0)
                continue;

            using var reader = new StringReader(fl.DynamicSqlText);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
                continue;

            var stmts = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();

            // A resolved dynamic "CREATE TRIGGER ..." is DDL, so it never becomes a DML
            // FlowLink below; record it separately so GraphExporter can model the trigger
            // as its own node with CREATES/ON edges (see docs/dynamic-trigger-modeling-spec.md).
            foreach (var ct in stmts.OfType<CreateTriggerStatement>())
            {
                var trig = ExtractTriggerCreation(ct, fl.LineNo);
                if (trig != null)
                    result.CreatedTriggers.Add(trig);
            }

            var dml = stmts.Where(s =>
                s is InsertStatement or UpdateStatement or DeleteStatement or
                SelectStatement or MergeStatement).ToList();
            if (dml.Count == 0)
                continue;

            var innerCtx = new WalkContext { Db = db, TableColumns = tableColumns };
            AstWalker.Walk(dml, new List<Condition>(), innerCtx, depth: 0);

            // Inherit the EXEC step's condition context so these resolved steps
            // appear in the same branch of the control flow as their EXEC.
            var resolved = innerCtx.FlowLinks.Select(inner => inner with
            {
                ConditionType = fl.ConditionType,
                ConditionText = fl.ConditionText,
                ConditionPath = fl.ConditionPath,
                ConditionKeys = fl.ConditionKeys,
                NestingLevel = fl.NestingLevel,
            }).ToList();

            if (resolved.Count > 0)
                toInsert.Add((i, resolved));
        }

        // Insert resolved links in reverse order to keep indices stable.
        foreach (var (afterIndex, links) in toInsert.OrderByDescending(t => t.afterIndex))
            result.FlowLinks.InsertRange(afterIndex + 1, links);
    }

    /// <summary>
    /// Pulls the "what trigger, on whom, when" out of a CREATE TRIGGER: its name, the table it
    /// fires ON, the timing (AFTER/INSTEAD OF/FOR) and the DML events (INSERT/UPDATE/DELETE).
    /// Returns null if the name or ON-table can't be read (defensive - never invents a node).
    /// </summary>
    private static TriggerCreationInfo? ExtractTriggerCreation(CreateTriggerStatement ct, int lineNo)
    {
        var name = SqlText.Generate(ct.Name);
        var onTable = SqlText.Generate(ct.TriggerObject?.Name);
        if (name.Length == 0 || onTable.Length == 0)
            return null;
        var events = (ct.TriggerActions ?? Array.Empty<TriggerAction>())
            .Select(a => a.TriggerActionType.ToString().ToUpperInvariant())
            .Distinct()
            .ToList();
        return new TriggerCreationInfo(name, onTable, ct.TriggerType.ToString(), events, lineNo);
    }

    private static string DetectObjectType(IList<TSqlStatement> topStatements)
    {
        foreach (var stmt in topStatements)
        {
            var t = stmt.GetType().Name;
            if (t.Contains("Procedure")) return "PROCEDURE";
            if (t.Contains("Trigger")) return "TRIGGER";
            if (t.Contains("View")) return "VIEW";
            if (t.Contains("Synonym")) return "SYNONYM";
            if (t.Contains("Function"))
            {
                // TableValuedFunctionReturnType → TVF; else scalar
                var ret = stmt.GetType().GetProperty("ReturnType")?.GetValue(stmt);
                return ret?.GetType().Name.Contains("Table") == true ? "TABLE_VALUED_FUNCTION" : "SCALAR_FUNCTION";
            }
        }
        return "UNKNOWN";
    }

    /// <summary>
    /// Finds the StatementList of the procedure/function/trigger body, regardless
    /// of whether the definition is CREATE / ALTER / CREATE OR ALTER (all of these
    /// statement classes expose a "StatementList" property of type StatementList).
    /// </summary>
    private static StatementList? FindBodyStatementList(IList<TSqlStatement> topStatements)
    {
        foreach (var stmt in topStatements)
        {
            var prop = stmt.GetType().GetProperty("StatementList");
            if (prop?.GetValue(stmt) is StatementList sl)
                return sl;
        }
        return null;
    }

    /// <summary>
    /// Finds a CREATE/ALTER VIEW's body SELECT via its "SelectStatement" property
    /// (present on CreateViewStatement/AlterViewStatement, but - unlike procedures/
    /// functions/triggers - never wrapped in a StatementList, so FindBodyStatementList
    /// never finds it). Matched by type name containing "View" so both CREATE and
    /// ALTER variants resolve without enumerating every concrete ScriptDom class.
    /// </summary>
    private static SelectStatement? FindViewSelect(IList<TSqlStatement> topStatements)
    {
        foreach (var stmt in topStatements)
        {
            if (stmt.GetType().Name.Contains("View") &&
                stmt.GetType().GetProperty("SelectStatement")?.GetValue(stmt) is SelectStatement sel)
                return sel;
        }
        return null;
    }

    /// <summary>The explicit column list of "CREATE VIEW v (Col1, Col2) AS ..." (empty when the view names its columns through the SELECT instead), used to name the view's output columns positionally.</summary>
    private static IReadOnlyList<string> FindViewColumns(IList<TSqlStatement> topStatements)
    {
        foreach (var stmt in topStatements)
        {
            if (stmt.GetType().Name.Contains("View") &&
                stmt.GetType().GetProperty("Columns")?.GetValue(stmt) is IEnumerable<Identifier> cols)
                return cols.Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Finds an inline table-valued function's body SELECT. An inline TVF
    /// (CREATE FUNCTION ... RETURNS TABLE AS RETURN (SELECT ...)) has no
    /// StatementList - its body lives on ReturnType as a SelectFunctionReturnType -
    /// so without this it falls through to the bare-DML path and is dropped,
    /// leaving the function with no read lineage at all.
    /// </summary>
    private static SelectStatement? FindInlineTableFunctionSelect(IList<TSqlStatement> topStatements)
    {
        foreach (var stmt in topStatements)
        {
            if (stmt.GetType().Name.Contains("Function") &&
                stmt.GetType().GetProperty("ReturnType")?.GetValue(stmt)
                    is SelectFunctionReturnType { SelectStatement: { } sel })
                return sel;
        }
        return null;
    }

    private static List<ParamInfo> FindParameters(IList<TSqlStatement> topStatements)
    {
        var result = new List<ParamInfo>();
        foreach (var stmt in topStatements)
        {
            var prop = stmt.GetType().GetProperty("Parameters");
            if (prop?.GetValue(stmt) is not System.Collections.IEnumerable list)
                continue;

            foreach (var p in list)
            {
                if (p is ProcedureParameter pp)
                {
                    result.Add(new ParamInfo(
                        pp.VariableName.Value,
                        SqlText.Generate(pp.DataType),
                        pp.Modifier == ParameterModifier.Output
                    ));
                }
            }
        }
        return result;
    }
}
