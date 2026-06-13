using System.Text;

namespace TSqlParser;

/// <summary>
/// Human-readable text reports built directly from the analyzed ObjectResult list
/// (the same data behind workflows_*.json), so no graph traversal is needed:
///  - GeneralReport: one overview of the whole database (inventory, flags, top
///    complexity, dynamic-SQL/cursor/transaction usage, parse errors).
///  - ObjectReport: one procedure/function/trigger in detail - parameters,
///    variables, references (called procs/functions, variable&lt;-column lineage),
///    and an indented control-flow graph (each step under the conditions that
///    govern it, nesting shown by indentation), plus the nested-rule outline.
/// </summary>
public static class ReportGenerator
{
    public static string GeneralReport(List<ObjectResult> results, List<TableSchemaResult> tableSchemas)
    {
        var sb = new StringBuilder();
        var ok = results.Where(r => r.Error == null).ToList();
        var db = results.Select(r => r.ObjectName.Split("::", 2)).FirstOrDefault(p => p.Length == 2)?[0] ?? "(varias)";

        sb.AppendLine("===================== INFORME GENERAL DE LA BASE DE DATOS =====================");
        sb.AppendLine($"Base de datos          : {db}");
        sb.AppendLine($"Objetos programables   : {results.Count}  ({ok.Count} ok, {results.Count - ok.Count} con error de parseo)");
        sb.AppendLine($"Tablas (CREATE TABLE)  : {tableSchemas.Count}");
        sb.AppendLine();
        sb.AppendLine("--- Caracteristicas ---");
        sb.AppendLine($"  Con transaccion       : {ok.Count(r => r.HasTransaction)}");
        sb.AppendLine($"  Con manejo de errores : {ok.Count(r => r.HasErrorHandling)}");
        sb.AppendLine($"  Con cursor            : {ok.Count(r => r.HasCursor)}");
        sb.AppendLine($"  Con SQL dinamico      : {ok.Count(r => r.DynamicSqlCount > 0)}");
        sb.AppendLine();

        sb.AppendLine("--- Top 10 por complejidad ciclomatica ---");
        foreach (var r in ok.OrderByDescending(r => r.ComplexityScore).Take(10))
            sb.AppendLine($"  cc={r.ComplexityScore,-3} dyn={r.DynamicSqlCount,-3} pasos={r.FlowLinks.Count,-3} {Plain(r.ObjectName)}");
        sb.AppendLine();

        sb.AppendLine("--- Tablas mas escritas (INSERT/UPDATE/DELETE/MERGE) ---");
        var writes = ok.SelectMany(r => r.FlowLinks)
            .Where(f => f.ConsequenceTarget.Length > 0 && f.ConsequenceType is "INSERT" or "UPDATE" or "DELETE" or "MERGE")
            .GroupBy(f => f.ConsequenceTarget).OrderByDescending(g => g.Count()).Take(10);
        foreach (var grp in writes)
            sb.AppendLine($"  {grp.Count(),-3} {grp.Key}");
        sb.AppendLine();

        var errored = results.Where(r => r.Error != null).ToList();
        if (errored.Count > 0)
        {
            sb.AppendLine("--- Objetos con error de parseo ---");
            foreach (var r in errored)
                sb.AppendLine($"  {Plain(r.ObjectName)}: {SqlText.Truncate(r.Error!, 100)}");
        }
        return sb.ToString();
    }

    public static string ObjectReport(ObjectResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("################ INFORME DE OBJETO ################");
        sb.AppendLine($"Objeto : {Plain(r.ObjectName)}");
        if (r.Error != null)
        {
            sb.AppendLine($"ERROR DE PARSEO: {r.Error}");
            return sb.ToString();
        }
        sb.AppendLine($"Flags  : transaccion={r.HasTransaction}  manejo_errores={r.HasErrorHandling}  cursor={r.HasCursor}  sql_dinamico={r.DynamicSqlCount}  complejidad={r.ComplexityScore}");

        sb.AppendLine();
        sb.AppendLine($"[PARAMETROS] ({r.Parameters.Count})");
        foreach (var p in r.Parameters)
            sb.AppendLine($"  {p.Name} {p.DataType}{(p.IsOutput ? " OUTPUT" : "")}");

        sb.AppendLine();
        sb.AppendLine($"[VARIABLES] ({r.Variables.Count})");
        foreach (var v in r.Variables)
            sb.AppendLine($"  {v.Name} {v.Type}");

        sb.AppendLine();
        sb.AppendLine("[REFERENCIAS]");
        sb.AppendLine($"  Procedimientos llamados (EXEC): {(r.ExecCalls.Count > 0 ? string.Join(", ", r.ExecCalls) : "-")}");
        sb.AppendLine($"  Funciones invocadas           : {(r.FunctionCalls.Count > 0 ? string.Join(", ", r.FunctionCalls) : "-")}");
        if (r.VariableAssignments.Count > 0)
        {
            sb.AppendLine("  Variables alimentadas desde columnas (lineage):");
            foreach (var va in r.VariableAssignments)
                sb.AppendLine($"    {va.VariableName} <- {va.SourceTable}({string.Join(", ", va.SourceColumns)})");
        }
        var reads = r.FlowLinks.Where(f => f.ConsequenceType == "SELECT" && f.ConsequenceTarget.Length > 0).Select(f => f.ConsequenceTarget).Distinct().ToList();
        var modifies = r.FlowLinks.Where(f => f.ConsequenceTarget.Length > 0 && f.ConsequenceType is "INSERT" or "UPDATE" or "DELETE" or "MERGE").Select(f => $"{f.ConsequenceType} {f.ConsequenceTarget}").Distinct().ToList();
        sb.AppendLine($"  Tablas leidas    : {(reads.Count > 0 ? string.Join(", ", reads) : "-")}");
        sb.AppendLine($"  Tablas escritas  : {(modifies.Count > 0 ? string.Join(", ", modifies) : "-")}");

        sb.AppendLine();
        sb.AppendLine($"[GRAFO DE CONTROL]  ({r.FlowLinks.Count} pasos - sangria = anidamiento, [DYN]=SQL dinamico)");
        for (int i = 0; i < r.FlowLinks.Count; i++)
        {
            var f = r.FlowLinks[i];
            var indent = new string(' ', 2 + f.NestingLevel * 2);
            var cond = f.ConditionType == "UNCONDITIONAL" ? "" : $"  «{f.ConditionType}: {SqlText.Truncate(Flatten(f.ConditionText), 70)}»";
            var dyn = f.DynamicSqlVars.Count > 0 ? " [DYN]" : "";
            var target = f.ConsequenceTarget.Length > 0 ? $" -> {f.ConsequenceTarget}" : "";
            sb.AppendLine($"{indent}#{i,-2} L{f.NestingLevel} {f.ConsequenceType}{dyn}{target}{cond}");
        }
        return sb.ToString();
    }

    private static string Plain(string objectName)
    {
        var parts = objectName.Split("::", 2);
        return parts.Length == 2 ? parts[1] : objectName;
    }

    private static string Flatten(string s) =>
        string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
}
