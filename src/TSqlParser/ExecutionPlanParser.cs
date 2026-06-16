using System.Xml.Linq;

namespace TSqlParser;

/// <summary>
/// Parses SQL Server execution plan XML files (.sqlplan / ShowPlanXML format)
/// and extracts table-level read/write references with actual row counts.
///
/// Works with both estimated plans (from SSMS "Display Estimated Plan" or
/// sys.dm_exec_query_plan) and actual plans (from "Include Actual Execution Plan"
/// or sys.dm_exec_cached_plans with runtime stats). Actual plans carry ActualRows
/// which is the gold-standard row count for lineage enrichment.
///
/// Namespace: http://schemas.microsoft.com/sqlserver/2004/07/showplan
/// Key elements used:
///   <StmtProc>   — stored procedure invocation (identifies the procedure)
///   <StmtSimple> — individual DML statement within the batch/procedure
///   <RelOp>      — relational operator node; PhysicalOp tells read vs write
///   <Object>     — table/index reference inside a RelOp (Database/Schema/Table attrs)
/// </summary>
public static class ExecutionPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // PhysicalOp values that represent writes to a table.
    private static readonly HashSet<string> WriteOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Clustered Index Insert", "Table Insert", "Index Insert",
        "Clustered Index Update", "Table Update", "Index Update",
        "Clustered Index Delete", "Table Delete", "Index Delete",
        "Table Merge", "Merge",
    };

    // System tables / internal SQL Server objects to exclude.
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys", "INFORMATION_SCHEMA", "msdb", "master",
    };

    public record PlanTableAccess(
        string Database,
        string Schema,
        string Table,
        string FullName,       // Database.Schema.Table (brackets stripped)
        bool   IsWrite,
        string Operation,      // PhysicalOp e.g. "Clustered Index Seek"
        long   EstimateRows,
        long   ActualRows,     // -1 if estimated plan (no ActualRows attr)
        bool   HasActualRows
    );

    public record PlanStatement(
        string StatementType,  // "SELECT", "INSERT", "UPDATE", etc.
        string StatementText,
        IList<PlanTableAccess> TableAccesses
    );

    public record PlanProcedure(
        string ProcedureName,  // schema.name or empty for ad-hoc batch
        IList<PlanStatement> Statements
    );

    public record ParsedPlan(
        string FileName,
        bool   IsActualPlan,
        IList<PlanProcedure> Procedures
    );

    /// <summary>
    /// Parses a SQL Server ShowPlanXML file (estimated or actual) and returns
    /// all procedure-level table accesses found in it.
    /// </summary>
    public static ParsedPlan Parse(string planXmlPath)
    {
        var xml = File.ReadAllText(planXmlPath);
        return ParseXml(xml, Path.GetFileName(planXmlPath));
    }

    public static ParsedPlan ParseXml(string xml, string sourceName = "<inline>")
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root == null)
            return new ParsedPlan(sourceName, false, Array.Empty<PlanProcedure>());

        // Detect actual vs estimated: actual plans have ActualRows on at least one RelOp.
        bool isActual = root.Descendants(Ns + "RelOp")
            .Any(r => r.Attribute("ActualRows") != null);

        var procedures = new List<PlanProcedure>();

        // Each <Batch> can contain one or more <Statements>.
        foreach (var batch in root.Descendants(Ns + "Batch"))
        {
            var stmtContainer = batch.Element(Ns + "Statements");
            if (stmtContainer == null) continue;

            foreach (var topStmt in stmtContainer.Elements())
            {
                if (topStmt.Name == Ns + "StmtProc")
                {
                    // Stored procedure call: identify the procedure and recurse
                    // into its nested <Statements> block.
                    var proc = ParseStmtProc(topStmt, isActual);
                    procedures.Add(proc);
                }
                else
                {
                    // Ad-hoc batch statement (not inside a proc): group under "".
                    var stmt = ParseStatement(topStmt, isActual);
                    if (stmt != null)
                    {
                        var adhoc = procedures.FirstOrDefault(p => p.ProcedureName == "");
                        if (adhoc == null)
                        {
                            adhoc = new PlanProcedure("", new List<PlanStatement>());
                            procedures.Add(adhoc);
                        }
                        ((List<PlanStatement>)adhoc.Statements).Add(stmt);
                    }
                }
            }
        }

        return new ParsedPlan(sourceName, isActual, procedures);
    }

    private static PlanProcedure ParseStmtProc(XElement stmtProc, bool isActual)
    {
        // <Procedure StatementText="CREATE PROCEDURE ..."> or <Procedure ObjectName="...">
        // The procedure reference is in the Procedure child element.
        var procElem = stmtProc.Element(Ns + "StoredProc") ?? stmtProc.Element(Ns + "Procedure");
        var procName = "";
        if (procElem != null)
        {
            var schemaName = Strip(procElem.Attribute("Schema")?.Value ?? "");
            var objName    = Strip(procElem.Attribute("ProcName")?.Value
                             ?? procElem.Attribute("ObjectName")?.Value
                             ?? procElem.Attribute("StatementText")?.Value
                             ?? "");
            if (schemaName.Length > 0 && objName.Length > 0)
                procName = $"{schemaName}.{objName}";
            else if (objName.Length > 0)
                procName = objName;
        }

        // Nested statements are in <Statements> inside the StmtProc.
        var stmts = new List<PlanStatement>();
        var innerStmts = stmtProc.Element(Ns + "Statements") ?? stmtProc;
        foreach (var child in innerStmts.Elements())
        {
            var parsed = ParseStatement(child, isActual);
            if (parsed != null)
                stmts.Add(parsed);
        }

        return new PlanProcedure(procName, stmts);
    }

    private static PlanStatement? ParseStatement(XElement stmtElem, bool isActual)
    {
        if (stmtElem.Name != Ns + "StmtSimple" && stmtElem.Name != Ns + "StmtCursor")
            return null;

        var stmtType = stmtElem.Attribute("StatementType")?.Value ?? "";
        var stmtText = stmtElem.Attribute("StatementText")?.Value ?? "";

        var accesses = new List<PlanTableAccess>();
        CollectTableAccesses(stmtElem, isActual, accesses);

        // Deduplicate: same table may appear in multiple RelOp nodes (e.g. index + clustered).
        // Keep max(ActualRows) per (table, isWrite) pair.
        var deduped = accesses
            .GroupBy(a => (a.FullName, a.IsWrite))
            .Select(g => g.OrderByDescending(a => a.ActualRows).First())
            .ToList();

        return new PlanStatement(stmtType, stmtText, deduped);
    }

    private static void CollectTableAccesses(XElement elem, bool isActual, List<PlanTableAccess> result)
    {
        foreach (var relOp in elem.Descendants(Ns + "RelOp"))
        {
            var physicalOp = relOp.Attribute("PhysicalOp")?.Value ?? "";
            var isWrite = WriteOps.Contains(physicalOp);

            var estimateRows = ParseLong(relOp.Attribute("EstimateRows")?.Value);
            var actualRows   = isActual
                ? ParseLong(relOp.Attribute("ActualRows")?.Value)
                : -1L;
            var hasActual = actualRows >= 0;

            // The <Object> element is a direct child (not deeper) when it belongs to this RelOp.
            foreach (var obj in relOp.Elements().SelectMany(e => e.Elements(Ns + "Object"))
                                     .Concat(relOp.Elements(Ns + "Object")))
            {
                var db     = Strip(obj.Attribute("Database")?.Value ?? "");
                var schema = Strip(obj.Attribute("Schema")?.Value ?? "");
                var table  = Strip(obj.Attribute("Table")?.Value ?? "");

                if (table.Length == 0)
                    continue;

                // Skip system schemas and internal worktables.
                if (SystemSchemas.Contains(schema))
                    continue;
                if (table.StartsWith("Worktable", StringComparison.OrdinalIgnoreCase) ||
                    table.StartsWith("Workfile", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullName = db.Length > 0
                    ? $"{db}.{schema}.{table}"
                    : schema.Length > 0 ? $"{schema}.{table}" : table;

                result.Add(new PlanTableAccess(db, schema, table, fullName,
                    isWrite, physicalOp, estimateRows, actualRows, hasActual));
            }
        }
    }

    private static string Strip(string s) =>
        s.Trim().TrimStart('[').TrimEnd(']');

    private static long ParseLong(string? s)
    {
        if (s == null) return -1;
        // EstimateRows can be a float like "1000.6" — truncate.
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (long)d;
        return -1;
    }

    /// <summary>
    /// Pretty summary of a parsed plan for console output.
    /// </summary>
    public static string Summarize(ParsedPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plan: {plan.FileName}  (actual={plan.IsActualPlan}, procs={plan.Procedures.Count})");
        foreach (var proc in plan.Procedures)
        {
            sb.AppendLine($"  Proc: {(proc.ProcedureName.Length > 0 ? proc.ProcedureName : "<ad-hoc>")}");
            foreach (var stmt in proc.Statements)
            {
                sb.AppendLine($"    [{stmt.StatementType}]");
                foreach (var t in stmt.TableAccesses)
                {
                    var rows = t.HasActualRows ? $" actual={t.ActualRows}" : $" est={t.EstimateRows}";
                    var rw   = t.IsWrite ? "WRITE" : "READ ";
                    sb.AppendLine($"      {rw}  {t.FullName}{rows}  ({t.Operation})");
                }
            }
        }
        return sb.ToString();
    }
}
