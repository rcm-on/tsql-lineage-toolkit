using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>
/// Recursively walks a procedure/function/trigger body, tracking the stack of
/// enclosing conditions (IF / WHILE / TRY-CATCH) so each "consequence" statement
/// (INSERT/UPDATE/DELETE/MERGE/EXEC/THROW/ALTER) can be linked back to the
/// condition that governs it - mirrors workflow_analyzer.FlowLink, but built on
/// the real AST so multi-line conditions, nested blocks and dynamic SQL
/// (EXEC(@sql)) are all handled correctly.
/// </summary>
public static class AstWalker
{
    public static void Walk(IList<TSqlStatement> statements, List<Condition> condStack, WalkContext ctx, int depth, HashSet<string>? cteNames = null, Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables = null)
    {
        cteNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cteBaseTables ??= new Dictionary<string, List<(string Alias, string Table)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var stmt in statements)
        {
            // "WITH cte AS (...) SELECT/INSERT/UPDATE/DELETE/MERGE ...": register the
            // CTE name(s) before processing the statement, so any table reference to
            // them below is recognized as a CTE alias, not a real table - and resolve
            // each CTE's own FROM clause to the real base table(s) it ultimately reads
            // from (recursing through earlier CTEs in the same WITH), so a later
            // reference to this CTE can be transparently expanded to those tables.
            CollectCteNames(stmt, cteNames, cteBaseTables);

            switch (stmt)
            {
                case BeginEndBlockStatement beb:
                    Walk(beb.StatementList.Statements, condStack, ctx, depth, cteNames, cteBaseTables);
                    break;

                case DeclareVariableStatement dv:
                    foreach (var el in dv.Declarations)
                    {
                        ctx.Variables.Add(new VariableInfo(
                            el.VariableName.Value,
                            SqlText.Generate(el.DataType),
                            el.Value != null ? SqlText.Truncate(SqlText.Generate(el.Value), 120) : ""
                        ));
                        if (el.Value != null)
                            TrackResolvedValue(ctx, el.VariableName.Value, el.Value);
                    }
                    break;

                case DeclareTableVariableStatement dtv:
                    ctx.Variables.Add(new VariableInfo(dtv.Body.VariableName.Value, "TABLE", ""));
                    break;

                case DeclareCursorStatement dcs:
                    ctx.HasCursor = true;
                    AddLink(ctx, condStack, "DECLARE_CURSOR", dcs.Name?.Value ?? "", stmt);
                    break;

                case SetVariableStatement svs:
                    if (svs.Variable?.Name != null && svs.Expression != null)
                    {
                        CollectAssignment(svs.Variable.Name, null, svs.Expression, ctx, cteNames, cteBaseTables);
                        RecordConstruction(ctx, svs.Variable.Name, svs.Expression);
                        TrackResolvedValue(ctx, svs.Variable.Name, svs.Expression);
                    }
                    break;

                case IfStatement ifs:
                    WalkIf(ifs, condStack, ctx, depth, cteNames, cteBaseTables);
                    break;

                case WhileStatement ws:
                    ctx.DecisionCount++;
                    var whileText = SqlText.Truncate(SqlText.Generate(ws.Predicate), 140);
                    condStack.Add(new Condition("WHILE", whileText, depth, ws.StartLine));
                    WalkSingleOrBlock(ws.Statement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
                    condStack.RemoveAt(condStack.Count - 1);
                    break;

                case TryCatchStatement tcs:
                    ctx.SawTryCatch = true;
                    ctx.DecisionCount++;
                    Walk(tcs.TryStatements.Statements, condStack, ctx, depth + 1, cteNames, cteBaseTables);
                    var catchStack = new List<Condition> { new("CATCH", "ON ERROR", -1, tcs.StartLine) };
                    Walk(tcs.CatchStatements.Statements, catchStack, ctx, depth + 1, cteNames, cteBaseTables);
                    break;

                case BeginTransactionStatement bts:
                    ctx.HasTransaction = true;
                    AddLink(ctx, condStack, "BEGIN_TRAN", bts.Name?.Value ?? "", stmt);
                    break;

                case CommitTransactionStatement cts:
                    ctx.HasTransaction = true;
                    AddLink(ctx, condStack, "COMMIT_TRAN", cts.Name?.Value ?? "", stmt);
                    break;

                case RollbackTransactionStatement rts:
                    ctx.HasTransaction = true;
                    AddLink(ctx, condStack, "ROLLBACK", rts.Name?.Value ?? "", stmt);
                    break;

                case ReturnStatement:
                    // Early-exit guards (e.g. "IF @Count = 0 RETURN;") were previously
                    // invisible: the variable got a DECLARES node but no Rule, because
                    // RETURN wasn't a tracked consequence. Now it is, with no target.
                    AddLink(ctx, condStack, "RETURN", "", stmt);
                    break;

                case InsertStatement ins:
                    {
                        var insTarget = TargetName(ins.InsertSpecification?.Target, cteNames);
                        var insColumns = InsertColumns(ins);
                        // No explicit column list: the statement targets every column
                        // of the table, in order (whether via "SELECT *" or "VALUES (...)").
                        if (insColumns.Count == 0)
                            insColumns = ResolveAllColumns(insTarget, ctx) ?? insColumns;
                        var (lineage, insExtraReads) = InsertSelectLineage(ins, insColumns, cteNames, cteBaseTables);
                        AddLink(ctx, condStack, "INSERT", insTarget, stmt, columns: insColumns, columnLineage: lineage, extraReads: insExtraReads);
                    }
                    break;

                case UpdateStatement upd:
                    AddLink(ctx, condStack, "UPDATE", TargetName(upd.UpdateSpecification?.Target, cteNames, upd.UpdateSpecification?.FromClause), stmt,
                        columns: UpdateColumns(upd));
                    break;

                case DeleteStatement del:
                    AddLink(ctx, condStack, "DELETE", TargetName(del.DeleteSpecification?.Target, cteNames, del.DeleteSpecification?.FromClause), stmt);
                    break;

                case MergeStatement mrg:
                    AddLink(ctx, condStack, "MERGE", TargetName(mrg.MergeSpecification?.Target, cteNames), stmt);
                    break;

                case AlterTableStatement alt:
                    AddLink(ctx, condStack, "ALTER", SqlText.Generate(alt.SchemaObjectName), stmt, detail: AlterDetail(alt));
                    break;

                case ExecuteStatement exec:
                    {
                        var (target, isDynamic, dynamicVars) = ExecTarget(exec);
                        // When the executed string reconstructs to a pure literal, surface
                        // *what* it runs (e.g. "CREATE PARTITION FUNCTION ...") - descriptive
                        // only; not re-parsed into lineage (see ResolveExecLiteral).
                        var dynText = isDynamic ? ResolveExecLiteral(exec, ctx) : "";
                        AddLink(ctx, condStack, "EXEC", target, stmt, isDynamic ? dynamicVars : null, dynamicSqlText: dynText);
                        if (!isDynamic && target.Length > 0)
                            ctx.ExecCalls.Add(target);
                        if (isDynamic)
                            ctx.DynamicSqlCount++;
                    }
                    break;

                case ThrowStatement:
                case RaiseErrorStatement:
                    AddLink(ctx, condStack, "THROW", "", stmt);
                    break;

                case SelectStatement sel:
                    // "SELECT @var = Col [, @var2 = Col2, ...] FROM T ...": each
                    // SelectSetVariable assigns a variable from a column of T,
                    // independent of whether the SELECT itself is tracked as a step.
                    if (sel.QueryExpression is QuerySpecification qsVars)
                    {
                        foreach (var setVar in qsVars.SelectElements.OfType<SelectSetVariable>())
                            if (setVar.Variable?.Name != null && setVar.Expression != null)
                            {
                                CollectAssignment(setVar.Variable.Name, qsVars.FromClause, setVar.Expression, ctx, cteNames, cteBaseTables);
                                RecordConstruction(ctx, setVar.Variable.Name, setVar.Expression);
                                // SELECT @v = <expr> FROM T: value comes from a row, never a
                                // pure literal -> drop any prior literal value for @v.
                                ctx.ResolvedVars.Remove(setVar.Variable.Name);
                            }
                    }

                    // Mirrors the Python analyzer's "SELECT ... FROM <table>" read-reference
                    // tracking: only SELECTs with a FROM clause are recorded as steps.
                    if (sel.QueryExpression is QuerySpecification { FromClause.TableReferences.Count: > 0 } qs)
                    {
                        var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
                        var selTarget = tableRefs.Count > 0 ? tableRefs[0].Table : "";
                        List<string> selColumns;
                        List<TableColumnRef> extraReads;

                        if (qs.SelectElements.Any(e => e is SelectStarExpression))
                        {
                            // "SELECT * FROM T" (or "SELECT t.*, ...") with no other columns
                            // resolved: expand "*" to T's full column list when known
                            // (single-table FROMs only - "*" across a JOIN can't be split).
                            selColumns = (tableRefs.Count == 1 ? ResolveAllColumns(selTarget, ctx) : null) ?? new List<string>();
                            extraReads = BuildExtraReads(tableRefs, new List<TableColumnRef>(), skipFirst: true);
                        }
                        else
                        {
                            var refs = new List<(string? Qualifier, string Column)>();
                            foreach (var el in qs.SelectElements)
                                if (el is SelectScalarExpression sse)
                                {
                                    var collector = new QualifiedColumnCollector();
                                    sse.Expression.Accept(collector);
                                    refs.AddRange(collector.Refs);
                                }
                            List<TableColumnRef> extras;
                            (selColumns, extras) = SplitColumnsByTable(refs, tableRefs);
                            extraReads = BuildExtraReads(tableRefs, extras, skipFirst: true);
                        }

                        AddLink(ctx, condStack, "SELECT", selTarget, stmt, columns: selColumns, extraReads: extraReads);
                    }
                    break;

                // Cursor lifecycle: previously invisible (only HasCursor flag). Now each
                // shows as a step so the flowchart reflects the real cursor loop scaffold
                // (DECLARE → OPEN → FETCH → WHILE … → CLOSE → DEALLOCATE).
                case OpenCursorStatement ocs:
                    AddLink(ctx, condStack, "OPEN_CURSOR", ocs.Cursor?.Name?.Value ?? "", stmt);
                    break;

                case FetchCursorStatement fcs:
                    AddLink(ctx, condStack, "FETCH", fcs.Cursor?.Name?.Value ?? "", stmt);
                    break;

                case CloseCursorStatement ccs:
                    AddLink(ctx, condStack, "CLOSE_CURSOR", ccs.Cursor?.Name?.Value ?? "", stmt);
                    break;

                case DeallocateCursorStatement dcz:
                    AddLink(ctx, condStack, "DEALLOCATE", dcz.Cursor?.Name?.Value ?? "", stmt);
                    break;

                // DDL that shapes the data the procedure works on - temp tables, indexes,
                // truncates, drops. Emitted as informational steps (no WRITES_TO edge, so
                // #temp names don't pollute the Table graph), giving the flowchart the
                // CREATE/DROP scaffolding it was missing.
                case CreateTableStatement cts2:
                    AddLink(ctx, condStack, "CREATE_TABLE", SqlText.Generate(cts2.SchemaObjectName), stmt);
                    break;

                case CreateIndexStatement cis:
                    AddLink(ctx, condStack, "CREATE_INDEX", cis.OnName != null ? SqlText.Generate(cis.OnName) : (cis.Name?.Value ?? ""), stmt);
                    break;

                case TruncateTableStatement tts:
                    AddLink(ctx, condStack, "TRUNCATE", tts.TableName != null ? SqlText.Generate(tts.TableName) : "", stmt);
                    break;

                case DropTableStatement dts:
                    AddLink(ctx, condStack, "DROP_TABLE", dts.Objects.Count > 0 ? SqlText.Generate(dts.Objects[0]) : "", stmt);
                    break;

                default:
                    // SET, PRINT, GOTO, etc.: not tracked as "consequences" (SET/DECLARE
                    // surface instead as Variable nodes, not control-flow steps).
                    break;
            }
        }
    }

    private static void WalkIf(IfStatement ifs, List<Condition> condStack, WalkContext ctx, int depth, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        ctx.DecisionCount++;
        var ifText = SqlText.Truncate(SqlText.Generate(ifs.Predicate), 140);

        condStack.Add(new Condition("IF", ifText, depth, ifs.StartLine));
        WalkSingleOrBlock(ifs.ThenStatement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
        condStack.RemoveAt(condStack.Count - 1);

        if (ifs.ElseStatement == null)
            return;

        // ELSE / ELSE IF: condition is "NOT(outer IF)". A chained ELSE IF is
        // itself an IfStatement, so WalkSingleOrBlock recurses into WalkIf,
        // which then pushes its own ("IF", <elseif predicate>, ...) on top -
        // giving each rung of the ladder both its negated-parent context and
        // its own positive condition.
        condStack.Add(new Condition("IF_ELSE", $"NOT ({ifText})", depth, ifs.StartLine));
        WalkSingleOrBlock(ifs.ElseStatement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
        condStack.RemoveAt(condStack.Count - 1);
    }

    private static void WalkSingleOrBlock(TSqlStatement stmt, List<Condition> condStack, WalkContext ctx, int depth, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        if (stmt is BeginEndBlockStatement beb)
            Walk(beb.StatementList.Statements, condStack, ctx, depth, cteNames, cteBaseTables);
        else
            Walk(new List<TSqlStatement> { stmt }, condStack, ctx, depth, cteNames, cteBaseTables);
    }

    /// <summary>
    /// Registers the names of any CTEs defined by a "WITH cte AS (...) SELECT/INSERT/
    /// UPDATE/DELETE/MERGE ..." statement, via reflection on WithCtesAndXmlNamespaces
    /// (present on SelectStatement and the *Specification of INSERT/UPDATE/DELETE/MERGE).
    /// Once registered, TargetName/TableRefName treat single-part references to these
    /// names as CTE aliases, not real tables. Additionally, for each CTE whose body is
    /// a single QuerySpecification (not a UNION/recursive CTE), resolves its FROM
    /// clause to the real base table(s) it ultimately reads from - expanding any
    /// earlier CTEs in the same WITH via cteBaseTables - so a later reference to this
    /// CTE can be transparently substituted with those real tables (see
    /// CollectTableRefsInto). CTEs whose body can't be resolved this way (UNION,
    /// recursive, etc.) are simply left out of cteBaseTables and contribute nothing
    /// when referenced - the same as an unresolvable derived table.
    /// </summary>
    private static void CollectCteNames(TSqlStatement stmt, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        WithCtesAndXmlNamespaces? ctes = stmt switch
        {
            SelectStatement sel => sel.WithCtesAndXmlNamespaces,
            InsertStatement or UpdateStatement or DeleteStatement or MergeStatement => GetCtes(stmt),
            _ => null,
        };

        if (ctes == null)
            return;

        foreach (var cte in ctes.CommonTableExpressions)
            cteNames.Add(cte.ExpressionName.Value);

        foreach (var cte in ctes.CommonTableExpressions)
        {
            if (cte.QueryExpression is QuerySpecification { FromClause: not null } qs)
                cteBaseTables[cte.ExpressionName.Value] = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
        }
    }

    private static WithCtesAndXmlNamespaces? GetCtes(TSqlStatement stmt) =>
        stmt.GetType().GetProperty("WithCtesAndXmlNamespaces")?.GetValue(stmt) as WithCtesAndXmlNamespaces;

    /// <summary>
    /// Short subtype label for an ALTER TABLE statement (e.g. "DROP PERIOD",
    /// "SET SYSTEM_VERSIONING=OFF", "ADD CONSTRAINT"), so two ALTERs on the same
    /// table (a common pattern for temporal tables: SET SYSTEM_VERSIONING=OFF
    /// followed by DROP PERIOD FOR SYSTEM_TIME) are distinguishable in the flow.
    /// Derived by regex over the generated SQL text rather than the AST subtype,
    /// to cover the many AlterTableXxxStatement shapes with one simple mapping.
    /// </summary>
    private static string AlterDetail(AlterTableStatement alt)
    {
        var sql = SqlText.Generate(alt);
        var m = Regex.Match(sql, @"ALTER\s+TABLE\s+\S+\s+(.*)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var rest = (m.Success ? m.Groups[1].Value : sql).Trim();

        if (Regex.IsMatch(rest, @"^DROP\s+PERIOD\s+FOR\s+SYSTEM_TIME", RegexOptions.IgnoreCase)) return "DROP PERIOD";
        if (Regex.IsMatch(rest, @"^ADD\s+PERIOD\s+FOR\s+SYSTEM_TIME", RegexOptions.IgnoreCase)) return "ADD PERIOD";
        if (Regex.IsMatch(rest, @"^SET\s*\(\s*SYSTEM_VERSIONING\s*=\s*ON", RegexOptions.IgnoreCase)) return "SET SYSTEM_VERSIONING=ON";
        if (Regex.IsMatch(rest, @"^SET\s*\(\s*SYSTEM_VERSIONING\s*=\s*OFF", RegexOptions.IgnoreCase)) return "SET SYSTEM_VERSIONING=OFF";
        if (Regex.IsMatch(rest, @"^ADD\s+CONSTRAINT", RegexOptions.IgnoreCase)) return "ADD CONSTRAINT";
        if (Regex.IsMatch(rest, @"^DROP\s+CONSTRAINT", RegexOptions.IgnoreCase)) return "DROP CONSTRAINT";
        if (Regex.IsMatch(rest, @"^ALTER\s+COLUMN", RegexOptions.IgnoreCase)) return "ALTER COLUMN";
        if (Regex.IsMatch(rest, @"^DROP\s+COLUMN", RegexOptions.IgnoreCase)) return "DROP COLUMN";
        if (Regex.IsMatch(rest, @"^ADD\s+", RegexOptions.IgnoreCase)) return "ADD COLUMN";
        if (Regex.IsMatch(rest, @"^(NO)?CHECK\s+CONSTRAINT", RegexOptions.IgnoreCase)) return "CHECK CONSTRAINT";
        if (Regex.IsMatch(rest, @"^(ENABLE|DISABLE)\s+TRIGGER", RegexOptions.IgnoreCase)) return "TOGGLE TRIGGER";
        if (Regex.IsMatch(rest, @"^REBUILD", RegexOptions.IgnoreCase)) return "REBUILD";
        if (Regex.IsMatch(rest, @"^SWITCH", RegexOptions.IgnoreCase)) return "SWITCH PARTITION";
        return "";
    }

    private static void AddLink(WalkContext ctx, List<Condition> condStack, string consequenceType, string target, TSqlStatement stmt, IReadOnlyList<string>? dynamicSqlVars = null, IReadOnlyList<string>? columns = null, IReadOnlyList<ColumnDerivation>? columnLineage = null, IReadOnlyList<TableColumnRef>? extraReads = null, string detail = "", string dynamicSqlText = "")
    {
        var (condType, condText) = condStack.Count > 0
            ? (condStack[^1].Type, condStack[^1].Text)
            : ("UNCONDITIONAL", "");

        var path = condStack.Select(c => $"{c.Type}: {c.Text}").ToList();
        // Parallel to path: a stable key per enclosing block instance ("TYPE#line"),
        // so two sibling blocks with identical condition text (e.g. two separate
        // "WHILE @@FETCH_STATUS = 0" cursor loops) don't collapse downstream.
        var keys = condStack.Select(c => $"{c.Type}#{c.BlockId}").ToList();
        var usedVariables = CollectVariableNames(stmt);

        ctx.FlowLinks.Add(new FlowLinkInfo(
            condType, condText, consequenceType, target,
            condStack.Count, stmt.StartLine, dynamicSqlVars, path, keys, columns, columnLineage, usedVariables, extraReads, detail, dynamicSqlText
        ));
    }

    /// <summary>
    /// Collects every real (alias -> table) pair reachable from a FROM clause,
    /// recursing through JOINs (both sides, in source order - tableRefs[0] is the
    /// same table TableRefName/TargetName would pick as "the" table for a
    /// single-table FROM), through derived tables/subqueries in FROM (their own
    /// inner FROM is flattened in - the derived table's own alias isn't itself a
    /// real table, so it contributes nothing, but its sources do), and through
    /// CTEs (substituted with cteBaseTables[name], i.e. THAT CTE's own already-
    /// flattened real base tables). Every entry's Table is therefore always a real
    /// table/view name - CTEs, derived tables and TVFs with no resolvable source
    /// simply contribute nothing, so a qualifier referencing their alias won't
    /// match any entry here and is safely dropped by SplitColumnsByTable.
    /// </summary>
    private static List<(string Alias, string Table)> CollectTableRefs(FromClause? fromClause, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var result = new List<(string Alias, string Table)>();
        if (fromClause == null)
            return result;
        foreach (var tref in fromClause.TableReferences)
            CollectTableRefsInto(tref, cteNames, cteBaseTables, result);
        return result;
    }

    private static void CollectTableRefsInto(TableReference tref, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables, List<(string Alias, string Table)> result)
    {
        switch (tref)
        {
            case NamedTableReference ntr when IsCte(ntr.SchemaObject, cteNames):
                if (cteBaseTables.TryGetValue(ntr.SchemaObject.BaseIdentifier.Value, out var baseTables))
                    foreach (var bt in baseTables)
                        if (!result.Contains(bt))
                            result.Add(bt);
                // Unresolvable CTE (UNION/recursive body): contributes nothing.
                break;
            case NamedTableReference ntr:
                var table = SqlText.Generate(ntr.SchemaObject);
                var alias = ntr.Alias?.Value ?? ntr.SchemaObject.BaseIdentifier?.Value ?? "";
                result.Add((alias, table));
                break;
            case QualifiedJoin qj:
                CollectTableRefsInto(qj.FirstTableReference, cteNames, cteBaseTables, result);
                CollectTableRefsInto(qj.SecondTableReference, cteNames, cteBaseTables, result);
                break;
            case UnqualifiedJoin uqj:
                // CROSS JOIN / CROSS APPLY / OUTER APPLY: no ON predicate, but both
                // sides still contribute real tables the same way as a QualifiedJoin.
                CollectTableRefsInto(uqj.FirstTableReference, cteNames, cteBaseTables, result);
                CollectTableRefsInto(uqj.SecondTableReference, cteNames, cteBaseTables, result);
                break;
            case QueryDerivedTable { QueryExpression: QuerySpecification { FromClause: not null } innerQs }:
                foreach (var innerTref in innerQs.FromClause.TableReferences)
                    CollectTableRefsInto(innerTref, cteNames, cteBaseTables, result);
                break;
            // TVFs, pivots, derived tables with a non-QuerySpecification body, etc.:
            // no alias->table mapping is possible, so columns qualified with their
            // alias simply won't resolve below.
        }
    }

    /// <summary>
    /// Builds the ExtraReads list for a step: every table in tableRefs (after
    /// tableRefs[0] when skipFirst, e.g. for a SELECT step where tableRefs[0]
    /// already becomes the step's primary ConsequenceTarget/READS_FROM; all of
    /// tableRefs when !skipFirst, e.g. for INSERT...SELECT which has no other
    /// READS_FROM mechanism for its source table(s)) gets a TableColumnRef -
    /// with whatever columns SplitColumnsByTable resolved for it (extrasFromCols),
    /// or an empty column list if none - so every JOIN/CTE/derived-table source
    /// still gets a READS_FROM edge even when no column could be attributed to it.
    /// </summary>
    private static List<TableColumnRef> BuildExtraReads(List<(string Alias, string Table)> tableRefs, List<TableColumnRef> extrasFromCols, bool skipFirst)
    {
        var result = new List<TableColumnRef>();
        var refs = skipFirst ? tableRefs.Skip(1) : tableRefs;
        foreach (var (_, table) in refs)
        {
            if (result.Any(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase)))
                continue;
            var match = extrasFromCols.FirstOrDefault(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase));
            result.Add(match ?? new TableColumnRef(table, Array.Empty<string>()));
        }
        return result;
    }

    /// <summary>
    /// Splits column references from a SELECT list (or a single SELECT expression)
    /// by which FROM/JOIN table they belong to, using each reference's qualifier
    /// (e.g. "b" in "b.Col2") resolved against tableRefs' aliases:
    /// - Qualified, alias resolves to a real table: attributed to that table (the
    ///   primary one - tableRefs[0] - goes in <c>primary</c>, others in <c>extras</c>).
    /// - Qualified, alias resolves to a CTE/derived table, or doesn't match any
    ///   FROM/JOIN alias: dropped (can't be traced to a real table).
    /// - Unqualified: attributed to tableRefs[0] only when it's the sole FROM table
    ///   (otherwise ambiguous - dropped rather than guessed).
    /// </summary>
    private static (List<string> Primary, List<TableColumnRef> Extras) SplitColumnsByTable(
        IEnumerable<(string? Qualifier, string Column)> refs, List<(string Alias, string Table)> tableRefs)
    {
        var primary = new List<string>();
        var primarySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extraCols = new List<(string Table, List<string> Columns, HashSet<string> Seen)>();

        foreach (var (qualifier, column) in refs)
        {
            string table;
            bool isPrimary;

            if (qualifier != null)
            {
                var matchIndex = tableRefs.FindIndex(t => string.Equals(t.Alias, qualifier, StringComparison.OrdinalIgnoreCase));
                if (matchIndex < 0 || tableRefs[matchIndex].Table.Length == 0)
                    continue;
                table = tableRefs[matchIndex].Table;
                isPrimary = matchIndex == 0;
            }
            else if (tableRefs.Count == 1 && tableRefs[0].Table.Length > 0)
            {
                table = tableRefs[0].Table;
                isPrimary = true;
            }
            else
            {
                continue;
            }

            if (isPrimary)
            {
                if (primarySeen.Add(column))
                    primary.Add(column);
                continue;
            }

            var entryIndex = extraCols.FindIndex(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase));
            if (entryIndex < 0)
            {
                extraCols.Add((table, new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                entryIndex = extraCols.Count - 1;
            }
            if (extraCols[entryIndex].Seen.Add(column))
                extraCols[entryIndex].Columns.Add(column);
        }

        return (primary, extraCols.Select(e => new TableColumnRef(e.Table, e.Columns)).ToList());
    }

    /// <summary>
    /// For "INSERT INTO T (Col1, Col2, ...) SELECT Expr1, Expr2, ... FROM S [JOIN J ...]":
    /// pairs each target column with the source table(s) and column(s) referenced by
    /// the corresponding SELECT expression - one ColumnDerivation per (target column,
    /// source table) pair, so "Col = s.A + j.B" yields two entries (one for S, one for
    /// J) instead of mis-attributing both to S. Column references are resolved via
    /// SplitColumnsByTable, same as the SELECT-step read tracking above. Returns empty
    /// when the source isn't a single QuerySpecification with a FROM clause, or the
    /// target/source element counts don't match (e.g. "SELECT *", UNION, computed
    /// columns added/dropped) - cases where a positional pairing would be a guess.
    /// </summary>
    /// <returns>
    /// Lineage: see summary above. ExtraReads: every table referenced by the SELECT's
    /// FROM/JOIN (after flattening CTEs/derived tables via CollectTableRefs) - including
    /// tableRefs[0], since unlike a SELECT step, INSERT...SELECT has no other READS_FROM
    /// for its source table(s) - paired with whatever columns could be attributed to it
    /// across ALL select elements (not just ones that line up 1:1 with insColumns), so
    /// e.g. "SELECT d.SumC FROM (SELECT A.Col1+B.Col2 ... FROM A CROSS JOIN B) d" still
    /// gives both A and B a READS_FROM even though "d.SumC" itself can't be attributed.
    /// </returns>
    private static (List<ColumnDerivation> Lineage, List<TableColumnRef> ExtraReads) InsertSelectLineage(InsertStatement ins, IReadOnlyList<string> insColumns, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var empty = (new List<ColumnDerivation>(), new List<TableColumnRef>());

        if (ins.InsertSpecification?.InsertSource is not SelectInsertSource { Select: QuerySpecification qs })
            return empty;

        if (qs.FromClause == null || qs.FromClause.TableReferences.Count == 0)
            return empty;

        var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
        if (tableRefs.Count == 0)
            return empty;

        var allRefs = new List<(string? Qualifier, string Column)>();
        foreach (var el in qs.SelectElements)
            if (el is SelectScalarExpression sse)
            {
                var collector = new QualifiedColumnCollector();
                sse.Expression.Accept(collector);
                allRefs.AddRange(collector.Refs);
            }
        var (_, extrasFromCols) = SplitColumnsByTable(allRefs, tableRefs);
        var extraReads = BuildExtraReads(tableRefs, extrasFromCols, skipFirst: false);

        var lineage = new List<ColumnDerivation>();
        if (insColumns.Count > 0 && qs.SelectElements.Count == insColumns.Count)
        {
            for (int i = 0; i < insColumns.Count; i++)
            {
                if (qs.SelectElements[i] is not SelectScalarExpression sse)
                    continue;

                var collector = new QualifiedColumnCollector();
                sse.Expression.Accept(collector);
                if (collector.Refs.Count == 0)
                    continue;

                var (primaryCols, extras) = SplitColumnsByTable(collector.Refs, tableRefs);
                if (primaryCols.Count > 0)
                    lineage.Add(new ColumnDerivation(insColumns[i], tableRefs[0].Table, primaryCols));
                foreach (var extra in extras)
                    lineage.Add(new ColumnDerivation(insColumns[i], extra.Table, extra.Columns));
            }
        }
        return (lineage, extraReads);
    }

    /// <summary>
    /// For "SET @var = expr" (fromClause null) or "SELECT @var = expr FROM S ...":
    /// if expr's column references can be traced to a single source table S, records
    /// a VariableAssignmentInfo(@var, S, columns). "SET @var = (SELECT Expr FROM S ...)"
    /// is unwrapped to the subquery's own SELECT expression and FROM clause first.
    /// No-ops when the source table can't be resolved (no FROM clause, expr has no
    /// column refs - e.g. a literal or another @variable, etc.).
    /// </summary>
    private static void CollectAssignment(string varName, FromClause? fromClause, ScalarExpression expr, WalkContext ctx, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        if (fromClause == null &&
            expr is ScalarSubquery { QueryExpression: QuerySpecification { SelectElements: [SelectScalarExpression { Expression: not null } sse] } subQs })
        {
            CollectAssignment(varName, subQs.FromClause, sse.Expression, ctx, cteNames, cteBaseTables);
            return;
        }

        if (fromClause == null || fromClause.TableReferences.Count == 0)
            return;

        var collector = new ColumnCollector();
        expr.Accept(collector);
        if (collector.Names.Count == 0)
            return;

        // TableRefName picks the first FROM table directly (fast path for the common
        // single-table case); falls back to CollectTableRefs (which expands CTEs and
        // derived tables to their real base tables) when that's not a plain table,
        // but only when it resolves to exactly one table - with a JOIN or multiple
        // CTE sources, which one the expression's column(s) actually come from is
        // ambiguous, so it's left unresolved rather than guessed.
        var sourceTable = TableRefName(fromClause.TableReferences[0], cteNames);
        if (sourceTable.Length == 0)
        {
            var tableRefs = CollectTableRefs(fromClause, cteNames, cteBaseTables);
            if (tableRefs.Count == 1)
                sourceTable = tableRefs[0].Table;
        }
        if (sourceTable.Length == 0)
            return;

        ctx.VariableAssignments.Add(new VariableAssignmentInfo(varName, sourceTable, collector.Names));
    }

    /// <summary>
    /// Records the textual right-hand side of an assignment to <paramref name="varName"/>
    /// (e.g. "'CREATE INDEX ' + @Name", "@SQL + ' ON dbo.T'") in source order, so the
    /// construction of a dynamic-SQL string can be reconstructed later. Truncated to
    /// keep the output bounded.
    /// </summary>
    private static void RecordConstruction(WalkContext ctx, string varName, ScalarExpression expr)
    {
        var text = SqlText.Truncate(SqlText.Generate(expr), 300);
        if (text.Length == 0)
            return;
        if (!ctx.VariableConstructions.TryGetValue(varName, out var list))
            ctx.VariableConstructions[varName] = list = new List<string>();
        list.Add(text);
    }

    /// <summary>
    /// Updates ctx.ResolvedVars with the current value of <paramref name="varName"/> when
    /// (and only when) its assigned expression reconstructs to a pure string literal -
    /// otherwise removes any prior value (it's now runtime-dependent). This is what lets a
    /// later dynamic EXEC show exactly which literal SQL it runs (see ResolveExecLiteral).
    /// </summary>
    private static void TrackResolvedValue(WalkContext ctx, string varName, ScalarExpression expr)
    {
        var lit = ResolveLiteral(expr, ctx);
        if (lit != null)
            ctx.ResolvedVars[varName] = lit.Length > 8000 ? lit[..8000] : lit;
        else
            ctx.ResolvedVars.Remove(varName);
    }

    /// <summary>
    /// Best-effort static evaluation of a scalar expression to the literal string it
    /// produces, supporting string literals, parentheses, literal-valued @variables and
    /// "a + b" concatenation of those. Returns null for anything runtime-dependent
    /// (column refs, params with no literal value, function calls, CASE, etc.).
    /// </summary>
    private static string? ResolveLiteral(ScalarExpression? expr, WalkContext ctx)
    {
        switch (expr)
        {
            case StringLiteral s:
                return s.Value;
            case ParenthesisExpression p:
                return ResolveLiteral(p.Expression, ctx);
            case VariableReference v:
                return ctx.ResolvedVars.TryGetValue(v.Name, out var val) ? val : null;
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } b:
                var l = ResolveLiteral(b.FirstExpression, ctx);
                var r = ResolveLiteral(b.SecondExpression, ctx);
                return l != null && r != null ? l + r : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// For a dynamic EXEC ("EXEC(@sql)" / "EXEC sp_executesql @sql, ..."), reconstructs the
    /// executed SQL when it resolves to a pure literal, returned whitespace-collapsed,
    /// USE-stripped and truncated for display. "" when built at runtime (the common case).
    /// Descriptive only: the result is NOT re-parsed into READS/WRITES/CALLS edges - a
    /// dataset audit showed the literal dynamic SQL here is admin DDL the walker doesn't
    /// track, so re-parsing would add labels but no lineage.
    /// </summary>
    private static string ResolveExecLiteral(ExecuteStatement exec, WalkContext ctx)
    {
        var entity = exec.ExecuteSpecification?.ExecutableEntity;
        string? sql = entity switch
        {
            // EXEC('...' + @x + ...): concatenate the string list.
            ExecutableStringList esl => ConcatLiterals(esl.Strings, ctx),
            // EXEC sp_executesql @sql, ...: the first parameter is the statement text.
            ExecutableProcedureReference epr when epr.Parameters?.Count > 0
                => ResolveLiteral(epr.Parameters[0].ParameterValue, ctx),
            _ => null,
        };
        if (sql == null)
            return "";
        var collapsed = Regex.Replace(sql, @"\s+", " ").Trim();
        collapsed = Regex.Replace(collapsed, @"^USE\s+\w+\s*;?\s*", "", RegexOptions.IgnoreCase);
        return SqlText.Truncate(collapsed, 200);
    }

    private static string? ConcatLiterals(IEnumerable<ScalarExpression> parts, WalkContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            var lit = ResolveLiteral(part, ctx);
            if (lit == null)
                return null;
            sb.Append(lit);
        }
        return sb.ToString();
    }

    /// <summary>Target columns of "INSERT INTO T (Col1, Col2, ...) VALUES/SELECT ..." - empty if not listed.</summary>
    private static List<string> InsertColumns(InsertStatement ins)
    {
        var cols = ins.InsertSpecification?.Columns;
        if (cols == null)
            return new List<string>();
        return cols.Select(ColumnName).Where(c => c.Length > 0).ToList();
    }

    /// <summary>Target columns of "UPDATE T SET Col1 = ..., Col2 = ..." - one per SET clause.</summary>
    private static List<string> UpdateColumns(UpdateStatement upd)
    {
        var result = new List<string>();
        foreach (var sc in upd.UpdateSpecification?.SetClauses ?? Enumerable.Empty<SetClause>())
        {
            if (sc is AssignmentSetClause { Column: not null } asc)
            {
                var name = ColumnName(asc.Column);
                if (name.Length > 0)
                    result.Add(name);
            }
        }
        return result;
    }

    private static string ColumnName(ColumnReferenceExpression cre)
    {
        var ids = cre.MultiPartIdentifier?.Identifiers;
        return ids is { Count: > 0 } ? ids[^1].Value : "";
    }

    /// <summary>Collects distinct column names from ColumnReferenceExpressions in a fragment (e.g. a SELECT element's expression, including inside function calls/CASE).</summary>
    private sealed class ColumnCollector : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Names { get; } = new();

        public override void Visit(ColumnReferenceExpression node)
        {
            var name = ColumnName(node);
            if (name.Length > 0 && _seen.Add(name))
                Names.Add(name);
        }
    }

    /// <summary>
    /// Collects (qualifier, column) pairs from ColumnReferenceExpressions in a fragment,
    /// preserving each reference's table/alias qualifier (e.g. ("b", "Col2") for
    /// "b.Col2"; (null, "Col1") for unqualified "Col1") so callers can resolve which
    /// FROM/JOIN table each one belongs to via SplitColumnsByTable. Not deduplicated -
    /// callers dedupe per resolved table via SplitColumnsByTable.
    /// </summary>
    private sealed class QualifiedColumnCollector : TSqlFragmentVisitor
    {
        public List<(string? Qualifier, string Column)> Refs { get; } = new();

        public override void Visit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is not { Count: > 0 })
                return;

            var column = ids[^1].Value;
            var qualifier = ids.Count > 1 ? ids[^2].Value : null;
            Refs.Add((qualifier, column));
        }
    }

    /// <summary>True if a (possibly schema-qualified) name is a single-part identifier matching a registered CTE alias.</summary>
    private static bool IsCte(SchemaObjectName name, HashSet<string> cteNames) =>
        name.Identifiers.Count == 1 && cteNames.Contains(name.BaseIdentifier.Value);

    /// <summary>
    /// Looks up the full column list of <paramref name="tableName"/> (a "schema.table"
    /// name as produced by TargetName/TableRefName) in ctx.TableColumns, scoped to
    /// ctx.Db. Returns null if the table is unknown (no CREATE TABLE was analyzed for
    /// it) or tableName is empty (CTE/table variable/derived table).
    /// </summary>
    private static List<string>? ResolveAllColumns(string tableName, WalkContext ctx)
    {
        if (tableName.Length == 0 || ctx.TableColumns == null)
            return null;

        var key = $"{ctx.Db}::{SqlText.NormalizeRef(tableName)}";
        return ctx.TableColumns.TryGetValue(key, out var cols) ? cols : null;
    }

    /// <summary>
    /// Resolves a table-modification target (INSERT/UPDATE/DELETE/MERGE) to its plain
    /// name - "" for anything that isn't a real table/view: a CTE alias, a table
    /// variable ("INSERT INTO @Table ..."), or any other non-named reference.
    /// For UPDATE/DELETE, "Target" is often itself just the alias used in the
    /// accompanying FROM clause (e.g. "UPDATE p SET ... FROM Production.Product p");
    /// when fromClause is given and Target is a single-part identifier, it's
    /// resolved against that FROM clause's table aliases first.
    /// </summary>
    private static string TargetName(TableReference? target, HashSet<string> cteNames, FromClause? fromClause = null)
    {
        if (target is not NamedTableReference ntr)
            return "";

        if (IsCte(ntr.SchemaObject, cteNames))
            return "";

        if (ntr.SchemaObject.Identifiers.Count == 1 && fromClause != null)
        {
            var resolved = ResolveAlias(ntr.SchemaObject.BaseIdentifier.Value, fromClause.TableReferences, cteNames);
            if (resolved != null)
                return resolved;
        }

        return SqlText.Generate(ntr.SchemaObject);
    }

    /// <summary>
    /// Searches a FROM clause's table references (recursing into JOINs) for a
    /// NamedTableReference aliased as <paramref name="alias"/>, and returns its
    /// real table name ("" if it's itself a CTE). Null if no such alias is found.
    /// </summary>
    private static string? ResolveAlias(string alias, IList<TableReference> refs, HashSet<string> cteNames)
    {
        foreach (var tref in refs)
        {
            switch (tref)
            {
                case NamedTableReference ntr when string.Equals(ntr.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase):
                    return IsCte(ntr.SchemaObject, cteNames) ? "" : SqlText.Generate(ntr.SchemaObject);
                case QualifiedJoin qj:
                    var found = ResolveAlias(alias, new[] { qj.FirstTableReference, qj.SecondTableReference }, cteNames);
                    if (found != null)
                        return found;
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// Like TargetName, but also unwraps JOINs (takes the left-most table) so a
    /// "SELECT ... FROM A JOIN B ON ..." reports A, matching the regex engine's
    /// "first table after FROM" behavior. Returns "" for anything that isn't a
    /// real table/view: a CTE alias, a table variable, a derived table
    /// ("FROM (SELECT ...) x"), a table-valued function call, etc.
    /// </summary>
    private static string TableRefName(TableReference tref, HashSet<string> cteNames) => tref switch
    {
        NamedTableReference ntr => IsCte(ntr.SchemaObject, cteNames) ? "" : SqlText.Generate(ntr.SchemaObject),
        QualifiedJoin qj => TableRefName(qj.FirstTableReference, cteNames),
        _ => "",
    };

    private static (string target, bool isDynamic, List<string> dynamicVars) ExecTarget(ExecuteStatement exec)
    {
        var entity = exec.ExecuteSpecification?.ExecutableEntity;
        switch (entity)
        {
            // EXEC sp_executesql @sql [, @params, ...]: a system proc that itself runs
            // dynamic SQL. Treat like EXECUTE (@sql) - the real target is unknowable
            // statically, but the @variables feeding it are.
            case ExecutableProcedureReference sprocRef
                when string.Equals(sprocRef.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                return ("(dynamic SQL via sp_executesql)", true, CollectVariableNames(sprocRef));

            case ExecutableProcedureReference epr when epr.ProcedureReference?.ProcedureReference?.Name != null:
                return (SqlText.Generate(epr.ProcedureReference.ProcedureReference.Name), false, new List<string>());
            case ExecutableStringList esl:
                return ("(dynamic SQL)", true, CollectVariableNames(esl));
            default:
                return (entity != null ? SqlText.Truncate(SqlText.Generate(entity), 80) : "", true,
                        entity != null ? CollectVariableNames(entity) : new List<string>());
        }
    }

    /// <summary>
    /// Walks a fragment (e.g. the string-literal/expression list of EXECUTE (@SQL))
    /// and returns the distinct @variable names referenced in it - i.e. the inputs
    /// that feed the dynamically-built SQL text, in source order.
    /// </summary>
    private static List<string> CollectVariableNames(TSqlFragment fragment)
    {
        var collector = new VariableCollector();
        fragment.Accept(collector);
        return collector.Names.ToList();
    }

    private sealed class VariableCollector : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Names { get; } = new();

        public override void Visit(VariableReference node)
        {
            if (_seen.Add(node.Name))
                Names.Add(node.Name);
        }
    }

    /// <summary>
    /// Walks an entire object body (any depth - SELECT lists, WHERE/JOIN predicates,
    /// computed columns, CASE branches, nested function args, etc.) and collects every
    /// FunctionCall's name, schema-qualified when written that way (e.g. "dbo.ufnGetStock").
    /// Built-in functions (GETDATE, COUNT, CAST, ...) come out unqualified and simply
    /// won't resolve to any analyzed object in GraphExporter, so no filtering is needed
    /// here - resolution against known objects is the filter.
    /// </summary>
    public sealed class FunctionCallCollector : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Names { get; } = new();

        public override void Visit(FunctionCall node)
        {
            // Sql160ScriptGenerator renders a MultiPartIdentifierCallTarget with its
            // own trailing "." (e.g. "Website."), so trim before joining to avoid
            // "Website..CalculateCustomerPrice".
            var name = node.CallTarget != null
                ? SqlText.Generate(node.CallTarget).TrimEnd('.') + "." + node.FunctionName.Value
                : node.FunctionName.Value;
            if (_seen.Add(name))
                Names.Add(name);
        }
    }
}
