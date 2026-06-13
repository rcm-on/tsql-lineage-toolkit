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

        result.Parameters.AddRange(FindParameters(topStatements));

        var statementList = FindBodyStatementList(topStatements);
        if (statementList == null)
            return result; // e.g. inline scalar function: RETURN <expr> with no BEGIN/END body

        var dbParts = name.Split("::", 2);
        var db = dbParts.Length == 2 ? dbParts[0] : "";
        var ctx = new WalkContext { Db = db, TableColumns = tableColumns };
        AstWalker.Walk(statementList.Statements, new List<Condition>(), ctx, depth: 0);

        result.Variables.AddRange(ctx.Variables);
        result.FlowLinks.AddRange(ctx.FlowLinks);
        result.ExecCalls.AddRange(ctx.ExecCalls.Distinct());
        result.VariableAssignments.AddRange(ctx.VariableAssignments);
        foreach (var kv in ctx.VariableConstructions)
            result.VariableConstructions[kv.Key] = kv.Value;

        var funcCollector = new AstWalker.FunctionCallCollector();
        statementList.Accept(funcCollector);
        result.FunctionCalls.AddRange(funcCollector.Names);

        result.HasTransaction = ctx.HasTransaction;
        result.HasErrorHandling = ctx.SawTryCatch;
        result.HasCursor = ctx.HasCursor;
        result.DynamicSqlCount = ctx.DynamicSqlCount;
        result.ComplexityScore = 1 + ctx.DecisionCount;

        return result;
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
