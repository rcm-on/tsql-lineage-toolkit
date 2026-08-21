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
    /// <summary>
    /// PROTOTYPE (dynsql-placeholder): marker wrapping a @parameter name when it
    /// substitutes for an unresolvable identifier inside QUOTENAME(...) (see
    /// ResolveLiteral's QUOTENAME case). Uses guillemets so it can never collide with a
    /// real identifier character T-SQL allows, and is easy to detect downstream
    /// (GraphExporter) via a simple Contains/regex check on the reconstructed name.
    /// </summary>
    internal const string PlaceholderPrefix = "«param:";
    internal const string PlaceholderSuffix = "»";

    public static void Walk(IList<TSqlStatement> statements, List<Condition> condStack, WalkContext ctx, int depth, HashSet<string>? cteNames = null, Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables = null)
    {
        // Only the top-level call for a trigger body seeds the pseudo-tables (nested
        // Walk recursions inherit the already-seeded maps). "inserted"/"deleted" behave
        // exactly like CTEs whose sole base table is the ON table: CollectTableRefs
        // substitutes them to it (so reads/writes and column qualifiers resolve to the
        // real table) and IsCte keeps them from ever surfacing as a real :Table node.
        var seedTrigger = cteNames == null && ctx.TriggerOnTable is { Length: > 0 };

        cteNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cteBaseTables ??= new Dictionary<string, List<(string Alias, string Table)>>(StringComparer.OrdinalIgnoreCase);

        if (seedTrigger)
        {
            var onTable = ctx.TriggerOnTable!;
            cteNames.Add("inserted");
            cteNames.Add("deleted");
            cteBaseTables["inserted"] = new List<(string Alias, string Table)> { ("inserted", onTable) };
            cteBaseTables["deleted"] = new List<(string Alias, string Table)> { ("deleted", onTable) };
        }

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
                    {
                        var varName = dtv.Body.VariableName.Value;
                        ctx.Variables.Add(new VariableInfo(varName, "TABLE", ""));

                        // Register the table variable's own column list so a later
                        // "SELECT * FROM @T"/"INSERT INTO @T" in the same body can
                        // resolve its columns (see WalkContext.TryGetColumns).
                        var cols = dtv.Body.Definition?.ColumnDefinitions
                            .Select(c => c.ColumnIdentifier.Value)
                            .ToList() ?? new List<string>();
                        ctx.RegisterTransientTable(varName, cols);
                    }
                    break;

                case DeclareCursorStatement dcs:
                    ctx.HasCursor = true;
                    AddLink(ctx, condStack, "DECLARE_CURSOR", dcs.Name?.Value ?? "", stmt);
                    // A cursor's SELECT reads real tables (often the whole point of the
                    // cursor: "DECLARE c CURSOR FOR SELECT ... FROM RealTable [UNION ...]").
                    // Previously only the cursor name was recorded, so those READS_FROM
                    // were lost entirely. Walk every QuerySpecification in the definition
                    // (UNION/parenthesized branches included) and emit its source reads.
                    foreach (var cursorFrom in QueryFromClauses(dcs.CursorDefinition?.Select?.QueryExpression))
                    {
                        var cursorRefs = CollectTableRefs(cursorFrom, cteNames, cteBaseTables);
                        if (cursorRefs.Count == 0)
                            continue;
                        var cursorExtraReads = BuildExtraReads(cursorRefs, new List<TableColumnRef>(), skipFirst: true);
                        AddLink(ctx, condStack, "SELECT", cursorRefs[0].Table, stmt, cteNames, cteBaseTables, extraReads: cursorExtraReads);
                    }
                    break;

                case SetVariableStatement svs:
                    if (svs.Variable?.Name != null && svs.Expression != null)
                    {
                        CollectAssignment(svs.Variable.Name, null, svs.Expression, ctx, cteNames, cteBaseTables);
                        RecordConstruction(ctx, svs.Variable.Name, svs.Expression);
                        TrackResolvedValue(ctx, svs.Variable.Name, svs.Expression);
                        WalkScalarSubqueryAssignment(svs.Variable.Name, svs.Expression, stmt, condStack, ctx, cteNames, cteBaseTables);
                    }
                    break;

                case IfStatement ifs:
                    EmitSubqueryReads(ifs.Predicate, ctx, condStack, ifs, cteNames, cteBaseTables);
                    WalkIf(ifs, condStack, ctx, depth, cteNames, cteBaseTables);
                    break;

                case WhileStatement ws:
                    EmitSubqueryReads(ws.Predicate, ctx, condStack, ws, cteNames, cteBaseTables);
                    ctx.DecisionCount++;
                    var whileText = SqlText.Truncate(SqlText.Generate(ws.Predicate), 140);
                    condStack.Add(new Condition("WHILE", whileText, depth, ws.StartLine));
                    try
                    {
                        WalkSingleOrBlock(ws.Statement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
                    }
                    finally
                    {
                        condStack.RemoveAt(condStack.Count - 1);
                    }
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
                        var (lineage, insExtraReads, insFilterInfo) = InsertSelectLineage(ins, insColumns, cteNames, cteBaseTables, ctx);
                        ProcessOutputClause((TSqlFragment?)ins.InsertSpecification?.OutputIntoClause ?? ins.InsertSpecification?.OutputClause, insTarget, ctx, cteNames, cteBaseTables, condStack, stmt);

                        // Mirror AddLink's own nested-subquery bridging (see its NestedTableRefs
                        // handling): since we pass filterColumnsOverride explicitly below, AddLink's
                        // auto-detect (and the merge that normally rides along with it) never runs,
                        // so a WHERE-nested EXISTS/IN table from insFilterInfo needs its own
                        // READS_FROM added here instead.
                        if (insFilterInfo.NestedTableRefs.Count > 0)
                        {
                            insExtraReads = insExtraReads.ToList();
                            foreach (var (_, table) in insFilterInfo.NestedTableRefs)
                                if (!insExtraReads.Any(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase)))
                                    insExtraReads.Add(new TableColumnRef(table, Array.Empty<string>()));
                        }

                        // "INSERT INTO T (...) EXEC [@var | proc] ...": the insert source
                        // is itself an EXEC (proc call or dynamic SQL via sp_executesql),
                        // not a SELECT/VALUES list - ScriptDom models this as an
                        // ExecuteInsertSource, so it never reaches the ExecuteStatement
                        // case below. Without this, the EXEC half (dynamic-SQL detection,
                        // the CALLS edge for a literal proc name, dynamic_sql text feeding
                        // ResolveDynamicSqlLinks) was silently dropped - only the INSERT
                        // target survived.
                        if (ins.InsertSpecification?.InsertSource is ExecuteInsertSource execSrc)
                        {
                            var insEntity = execSrc.Execute?.ExecutableEntity;
                            var (insExecTarget, insIsDynamic, insDynamicVars) = ExecTarget(insEntity);
                            var insDynText = insIsDynamic ? ResolveExecLiteral(insEntity, ctx) : "";
                            AddLink(ctx, condStack, "INSERT", insTarget, stmt, cteNames, cteBaseTables,
                                insIsDynamic ? insDynamicVars : null,
                                columns: insColumns, columnLineage: lineage, extraReads: insExtraReads,
                                dynamicSqlText: insDynText);
                            if (!insIsDynamic && insExecTarget.Length > 0)
                                ctx.ExecCalls.Add(insExecTarget);
                            if (insIsDynamic)
                                ctx.DynamicSqlCount++;
                        }
                        else
                        {
                            AddLink(ctx, condStack, "INSERT", insTarget, stmt, cteNames, cteBaseTables, columns: insColumns, columnLineage: lineage, extraReads: insExtraReads,
                                filterColumnsOverride: insFilterInfo.FilterColumns, filterOpKindsOverride: insFilterInfo.FilterOpKinds,
                                filterTextOverride: insFilterInfo.FilterText, filterKindOverride: insFilterInfo.FilterKind);
                        }
                    }
                    break;

                case UpdateStatement upd:
                    {
                        var updTarget = TargetName(upd.UpdateSpecification?.Target, cteNames, upd.UpdateSpecification?.FromClause, cteBaseTables);
                        var updColumns = UpdateColumns(upd);
                        List<TableColumnRef>? updExtraReads = null;
                        var updFrom = upd.UpdateSpecification?.FromClause;
                        if (updFrom != null && updTarget.Length > 0)
                        {
                            var allRefs = CollectTableRefs(updFrom, cteNames, cteBaseTables);
                            var partners = allRefs
                                .Where(r => !string.Equals(r.Table, updTarget, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (partners.Count > 0)
                                updExtraReads = BuildExtraReads(partners, new List<TableColumnRef>(), skipFirst: false);
                        }

                        ProcessOutputClause((TSqlFragment?)upd.UpdateSpecification?.OutputIntoClause ?? upd.UpdateSpecification?.OutputClause, updTarget, ctx, cteNames, cteBaseTables, condStack, stmt);

                        var updLineage = UpdateSetLineage(upd, updTarget, cteNames, cteBaseTables);
                        AddLink(ctx, condStack, "UPDATE", updTarget, stmt, cteNames, cteBaseTables, columns: updColumns, columnLineage: updLineage, extraReads: updExtraReads);
                    }
                    break;

                case DeleteStatement del:
                    {
                        var delTarget = TargetName(del.DeleteSpecification?.Target, cteNames, del.DeleteSpecification?.FromClause, cteBaseTables);
                        List<TableColumnRef>? delExtraReads = null;

                        // "DELETE t FROM TargetTable t JOIN Other o ON ...": Other is read
                        // (to decide which rows of TargetTable to delete) but never written.
                        if (del.DeleteSpecification?.FromClause != null)
                        {
                            var allRefs = CollectTableRefs(del.DeleteSpecification.FromClause, cteNames, cteBaseTables);
                            var partners = allRefs
                                .Where(r => !string.Equals(r.Table, delTarget, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (partners.Count > 0)
                                delExtraReads = BuildExtraReads(partners, new List<TableColumnRef>(), skipFirst: false);
                        }

                        ProcessOutputClause((TSqlFragment?)del.DeleteSpecification?.OutputIntoClause ?? del.DeleteSpecification?.OutputClause, delTarget, ctx, cteNames, cteBaseTables, condStack, stmt);
                        AddLink(ctx, condStack, "DELETE", delTarget, stmt, cteNames, cteBaseTables, extraReads: delExtraReads);
                    }
                    break;

                case MergeStatement mrg:
                    {
                        var mrgTarget = TargetName(mrg.MergeSpecification?.Target, cteNames);
                        List<TableColumnRef>? mrgExtraReads = null;

                        // Target first (so t.-qualified / unqualified refs resolve to it), then
                        // USING source(s) - built once here and threaded into MergeLineage/the ON
                        // clause below, instead of each rebuilding its own copy.
                        var mrgTableRefs = new List<(string Alias, string Table)>
                            { (mrg.MergeSpecification?.TableAlias?.Value ?? mrgTarget, mrgTarget) };

                        // MERGE's source ("USING <TableReference> ...") can itself be a JOIN,
                        // unlike Target - so it's flattened the same way a FROM clause is,
                        // rather than resolved as a single TargetName.
                        if (mrg.MergeSpecification?.TableReference != null)
                        {
                            var refs = new List<(string Alias, string Table)>();
                            CollectTableRefsInto(mrg.MergeSpecification.TableReference, cteNames, cteBaseTables, refs);
                            foreach (var r in refs)
                                if (!mrgTableRefs.Any(x => string.Equals(x.Alias, r.Alias, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Table, r.Table, StringComparison.OrdinalIgnoreCase)))
                                    mrgTableRefs.Add(r);
                            mrgExtraReads = BuildExtraReads(refs, new List<TableColumnRef>(), skipFirst: false);
                        }

                        ProcessOutputClause((TSqlFragment?)mrg.MergeSpecification?.OutputIntoClause ?? mrg.MergeSpecification?.OutputClause, mrgTarget, ctx, cteNames, cteBaseTables, condStack, stmt);

                        var mrgLineage = MergeLineage(mrg, mrgTarget, mrgTableRefs);
                        // WRITES_COLUMN must cover every column actually assigned by a WHEN
                        // MATCHED UPDATE SET / WHEN NOT MATCHED INSERT column list, not just the
                        // ones MergeLineage could trace back to a real source table - mirrors
                        // UpdateColumns()/InsertColumns() for a plain UPDATE/INSERT, which never
                        // conditioned WRITES_COLUMN on DERIVES_FROM succeeding. Without this, a
                        // "WHEN MATCHED THEN UPDATE SET Col = q.Val" whose USING source is a
                        // derived table of only variables/parameters (no real base table to
                        // resolve q.Val to - e.g. dbo.UpdateHostSetting in the DNN corpus, see
                        // eval/column-recall/blind-refs.md causa #2) silently dropped Col from the
                        // graph entirely: no DERIVES_FROM (nothing to trace) AND no WRITES_COLUMN
                        // (mrgColumns used to come only from mrgLineage).
                        var mrgColumns = mrgLineage.Select(d => d.TargetColumn)
                            .Concat(MergeTargetColumns(mrg))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        // ON clause -> FILTERS_ON: "what decided which rows got matched", same
                        // role as a JOIN's ON predicate elsewhere (also FILTERS_ON, never a
                        // :BusinessRule - see ExtractFilterColumnsCore's WHERE-only FilterText
                        // note) - so filterTextOverride is deliberately left unset here. Resolved
                        // against the same mrgTableRefs (target + flattened USING source(s)) as
                        // MergeLineage above; an alias that isn't a real table (e.g. a USING
                        // derived table built only from variables) is silently dropped rather than
                        // guessed, same "don't fabricate" contract SplitColumnsByTable already
                        // uses everywhere else.
                        List<TableColumnRef>? mrgFilterColumns = null;
                        List<string>? mrgFilterOpKinds = null;
                        if (mrg.MergeSpecification?.SearchCondition != null)
                        {
                            var onCollector = new QualifiedColumnCollector();
                            mrg.MergeSpecification.SearchCondition.Accept(onCollector);
                            if (onCollector.Refs.Count > 0)
                            {
                                var (onPrimary, onExtras) = SplitColumnsByTable(onCollector.Refs, mrgTableRefs);
                                var onFilters = new List<TableColumnRef>();
                                if (onPrimary.Count > 0)
                                    onFilters.Add(new TableColumnRef(mrgTableRefs[0].Table, onPrimary));
                                onFilters.AddRange(onExtras);
                                if (onFilters.Count > 0)
                                {
                                    mrgFilterColumns = onFilters;
                                    mrgFilterOpKinds = OperatorClassifier.Classify(mrg.MergeSpecification.SearchCondition).ToList();
                                }
                            }
                        }

                        AddLink(ctx, condStack, "MERGE", mrgTarget, stmt, cteNames, cteBaseTables, columns: mrgColumns, columnLineage: mrgLineage, extraReads: mrgExtraReads,
                            filterColumnsOverride: mrgFilterColumns, filterOpKindsOverride: mrgFilterOpKinds);
                    }
                    break;

                case AlterTableStatement alt:
                    AddLink(ctx, condStack, "ALTER", SqlText.Generate(alt.SchemaObjectName), stmt, cteNames, cteBaseTables, detail: AlterDetail(alt), columns: AlterColumns(alt));
                    break;

                case ExecuteStatement exec:
                    {
                        var execEntity = exec.ExecuteSpecification?.ExecutableEntity;
                        var (target, isDynamic, dynamicVars) = ExecTarget(execEntity);
                        // When the executed string reconstructs to a pure literal, surface
                        // *what* it runs (e.g. "CREATE PARTITION FUNCTION ..."). Kept FULL here;
                        // SqlAnalyzer.ResolveDynamicSqlLinks re-parses it for the inner DML's
                        // lineage, and the display copy is truncated later in GraphExporter.
                        var dynText = isDynamic ? ResolveExecLiteral(execEntity, ctx) : "";
                        AddLink(ctx, condStack, "EXEC", target, stmt, cteNames, cteBaseTables, isDynamic ? dynamicVars : null, dynamicSqlText: dynText);
                        if (!isDynamic && target.Length > 0)
                            ctx.ExecCalls.Add(target);
                        if (isDynamic)
                            ctx.DynamicSqlCount++;
                    }
                    break;

                case ThrowStatement:
                    AddLink(ctx, condStack, "THROW", "", stmt);
                    break;

                case RaiseErrorStatement res:
                    // RAISERROR's severity (2nd parameter) decides whether this is a real
                    // error (breaks flow, can be caught by TRY/CATCH) or just an
                    // informational message to the client - severity <= 10 is
                    // informational per T-SQL semantics (RAISERROR(..., 10, 1) WITH NOWAIT
                    // is the standard idiom for progress messages, e.g. Ola Hallengren's
                    // maintenance scripts). Only a literal integer severity can be
                    // classified statically; a variable/expression severity (e.g.
                    // RAISERROR(@msg, @sev, 1)) fails closed as THROW, since it may resolve
                    // to a real error at runtime and silently downgrading it to PRINT would
                    // reintroduce a false negative.
                    var isInformational = res.SecondParameter is IntegerLiteral sevLit
                        && int.TryParse(sevLit.Value, out var severity)
                        && severity <= 10;
                    AddLink(ctx, condStack, isInformational ? "PRINT" : "THROW", "", stmt);
                    break;

                case PrintStatement:
                    // PRINT never breaks flow and never throws - it's the direct
                    // informational counterpart to RAISERROR(..., <=10, ...) above, so it
                    // shares the same "PRINT" action name rather than inventing a second
                    // synonym for "informational output".
                    AddLink(ctx, condStack, "PRINT", "", stmt);
                    break;

                case BreakStatement:
                    AddLink(ctx, condStack, "BREAK", "", stmt);
                    break;

                case ContinueStatement:
                    AddLink(ctx, condStack, "CONTINUE", "", stmt);
                    break;

                case GoToStatement gts:
                    AddLink(ctx, condStack, "GOTO", gts.LabelName?.Value ?? "", stmt);
                    break;

                case WaitForStatement:
                    AddLink(ctx, condStack, "WAITFOR", "", stmt);
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

                    // "SELECT ... INTO #Target FROM Source [JOIN ...]":
                    // creates and populates #Target - treated as INSERT (WRITES_TO #Target)
                    // with all FROM tables as ExtraReads (READS_FROM each source).
                    if (sel.Into != null)
                    {
                        var intoName = SqlText.Generate(sel.Into);
                        if (sel.QueryExpression is QuerySpecification qsInto)
                        {
                            var tableRefs = CollectTableRefs(qsInto.FromClause, cteNames, cteBaseTables);
                            var extraReads = BuildExtraReads(tableRefs, new List<TableColumnRef>(), skipFirst: false);
                            AddLink(ctx, condStack, "INSERT", intoName, stmt, extraReads: extraReads);
                        }
                        else
                        {
                            AddLink(ctx, condStack, "INSERT", intoName, stmt);
                        }
                    }
                    // "SELECT ... FROM <table>" read-reference tracking.
                    // Only SELECTs with a FROM clause are recorded as steps.
                    else if (sel.QueryExpression is QuerySpecification { FromClause.TableReferences.Count: > 0 } qs)
                    {
                        var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
                        var selTarget = tableRefs.Count > 0 ? tableRefs[0].Table : "";
                        List<string> selColumns;
                        List<TableColumnRef> extraReads;

                        var starExprs = qs.SelectElements.OfType<SelectStarExpression>().ToList();
                        var isSelectStar = starExprs.Count > 0;
                        if (isSelectStar)
                        {
                            // "SELECT * FROM T": expand to T's full column list when known.
                            //
                            // Con varias tablas en el FROM esto se rendía y devolvía la lista
                            // vacía, que es justo la forma más común en código real:
                            // "SELECT jc.*, u.* FROM A jc JOIN B u" o "SELECT R.*, UR.IsOwner
                            // FROM ...". Medido sobre el corpus DNN, rendirse ahí perdía 287
                            // columnas en 23 módulos. Ahora un "*" sin cualificar expande TODAS
                            // las tablas del FROM y un "alias.*" expande sólo la tabla a la que
                            // ese alias apunta.
                            var hasUnqualifiedStar = starExprs.Any(s => s.Qualifier is not { Identifiers.Count: > 0 });
                            var starQualifiers = starExprs
                                .Where(s => s.Qualifier is { Identifiers.Count: > 0 })
                                .Select(s => s.Qualifier!.Identifiers[^1].Value)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            // El cualificador puede ser el alias ("jc") o el propio nombre de la
                            // tabla, con o sin esquema ("dbo.Users" -> "Users").
                            bool StarCovers((string Alias, string Table) tr) =>
                                hasUnqualifiedStar
                                || starQualifiers.Contains(tr.Alias)
                                || starQualifiers.Contains(tr.Table)
                                || starQualifiers.Contains(SqlText.NormalizeRef(tr.Table).Split('.')[^1]);

                            selColumns = (tableRefs.Count > 0 && StarCovers(tableRefs[0])
                                ? ResolveAllColumns(selTarget, ctx)
                                : null) ?? new List<string>();

                            var starExtras = new List<TableColumnRef>();
                            foreach (var tr in tableRefs.Skip(1))
                                if (StarCovers(tr) && ResolveAllColumns(tr.Table, ctx) is { Count: > 0 } starCols)
                                    starExtras.Add(new TableColumnRef(tr.Table, starCols.ToArray()));

                            // Una estrella casi nunca viene sola: "SELECT el.*, ee.PortalId,
                            // e.Message FROM ..." es la forma normal de ampliar una tabla con
                            // campos de sus JOINs. Esta rama solo expandía la estrella y
                            // DESCARTABA las columnas escritas explícitamente al lado.
                            // Medido sobre el corpus DNN: la vista vw_EventLog y los tres
                            // procedimientos que la leen perdían 12 columnas cada uno, justo
                            // las 12 explícitas que siguen a "el.*".
                            var explicitRefs = new List<(string? Qualifier, string Column)>();
                            foreach (var el in qs.SelectElements)
                            {
                                var expr = el switch
                                {
                                    SelectScalarExpression sse => sse.Expression,
                                    SelectSetVariable ssv      => ssv.Expression,
                                    _                          => null,
                                };
                                if (expr == null)
                                    continue;
                                var explicitCollector = new QualifiedColumnCollector();
                                expr.Accept(explicitCollector);
                                explicitRefs.AddRange(explicitCollector.Refs);
                            }

                            if (explicitRefs.Count > 0)
                            {
                                var (explicitPrimary, explicitExtras) =
                                    SplitColumnsByTable(explicitRefs, tableRefs, tn => ResolveAllColumns(tn, ctx));

                                foreach (var c in explicitPrimary)
                                    if (!selColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                                        selColumns.Add(c);

                                foreach (var ex in explicitExtras)
                                {
                                    var idx = starExtras.FindIndex(s => string.Equals(s.Table, ex.Table, StringComparison.OrdinalIgnoreCase));
                                    if (idx < 0)
                                        starExtras.Add(ex);
                                    else
                                        starExtras[idx] = new TableColumnRef(
                                            ex.Table,
                                            starExtras[idx].Columns.Union(ex.Columns, StringComparer.OrdinalIgnoreCase).ToArray());
                                }
                            }

                            extraReads = BuildExtraReads(tableRefs, starExtras, skipFirst: true);
                            FusionarLecturas(extraReads, ScopedRefsFromSelectList(qs, tableRefs, cteNames, cteBaseTables, ctx));
                        }
                        else
                        {
                            var refs = new List<(string? Qualifier, string Column)>();
                            foreach (var el in qs.SelectElements)
                            {
                                // SelectSetVariable es "SELECT @v = Col FROM t", la forma
                                // canonica de leer una fila a variables. ScriptDom NO lo modela
                                // como SelectScalarExpression, asi que mirar solo ese tipo
                                // perdia la lista de seleccion entera: en el corpus DNN, 58
                                // columnas en 24 modulos de las que el motor solo veia el WHERE.
                                var expr = el switch
                                {
                                    SelectScalarExpression sse => sse.Expression,
                                    SelectSetVariable ssv      => ssv.Expression,
                                    _                          => null,
                                };
                                if (expr == null)
                                    continue;
                                var collector = new QualifiedColumnCollector();
                                expr.Accept(collector);
                                refs.AddRange(collector.Refs);
                            }
                            List<TableColumnRef> extras;
                            (selColumns, extras) = SplitColumnsByTable(refs, tableRefs, tn => ResolveAllColumns(tn, ctx));
                            // CROSS/OUTER APPLY xmlcol.nodes() shreds an XML column: that
                            // column is genuinely read, but its only mention is the apply
                            // target (a function table reference), invisible to the select
                            // list above. Add each apply's base column as a read.
                            foreach (var src in BuildXmlApplyMap(qs.FromClause, tableRefs).Values)
                                if (string.Equals(src.Table, selTarget, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!selColumns.Contains(src.Column, StringComparer.OrdinalIgnoreCase))
                                        selColumns.Add(src.Column);
                                }
                                else
                                {
                                    var idx = extras.FindIndex(e => string.Equals(e.Table, src.Table, StringComparison.OrdinalIgnoreCase));
                                    if (idx < 0)
                                        extras.Add(new TableColumnRef(src.Table, new[] { src.Column }));
                                    else if (!extras[idx].Columns.Contains(src.Column, StringComparer.OrdinalIgnoreCase))
                                        extras[idx] = new TableColumnRef(src.Table, extras[idx].Columns.Append(src.Column).ToArray());
                                }
                            extraReads = BuildExtraReads(tableRefs, extras, skipFirst: true);
                            FusionarLecturas(extraReads, ScopedRefsFromSelectList(qs, tableRefs, cteNames, cteBaseTables, ctx));
                        }

                        AddLink(ctx, condStack, "SELECT", selTarget, stmt, cteNames, cteBaseTables, columns: selColumns, extraReads: extraReads, selectStar: isSelectStar);
                    }
                    // Top-level set operation (UNION/EXCEPT/INTERSECT) or a parenthesized
                    // query: the branch above only covers a single QuerySpecification, so
                    // its source reads were dropped. Walk each branch's FROM for its reads -
                    // and (via FlattenQuerySpecifications, which also exposes the branch's own
                    // QuerySpecification rather than just its FromClause) that branch's own
                    // WHERE, previously dropped the same way a CTE branch's WHERE was: AddLink's
                    // own auto-detect only looks at qs.WhereClause for a plain
                    // "SelectStatement { QueryExpression: QuerySpecification }", never for one
                    // branch of a BinaryQueryExpression, so "SELECT ... WHERE X UNION SELECT ...
                    // WHERE Y" produced zero FILTERS_ON for either X or Y.
                    else if (sel.QueryExpression is BinaryQueryExpression or QueryParenthesisExpression)
                    {
                        foreach (var setQs in FlattenQuerySpecifications(sel.QueryExpression))
                        {
                            if (setQs.FromClause == null)
                                continue;
                            var setRefs = CollectTableRefs(setQs.FromClause, cteNames, cteBaseTables);
                            if (setRefs.Count == 0)
                                continue;
                            var setExtra = BuildExtraReads(setRefs, new List<TableColumnRef>(), skipFirst: true);
                            var (setFilterColumns, _, setFilterOpKinds, setFilterText, setFilterKind) =
                                ExtractFilterColumnsCore(setQs.WhereClause, setQs.FromClause.TableReferences, setRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));
                            AddLink(ctx, condStack, "SELECT", setRefs[0].Table, stmt, cteNames, cteBaseTables, extraReads: setExtra,
                                filterColumnsOverride: setFilterColumns, filterOpKindsOverride: setFilterOpKinds,
                                filterTextOverride: setFilterText, filterKindOverride: setFilterKind);
                        }
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
                    {
                        var tableName = SqlText.Generate(cts2.SchemaObjectName);
                        AddLink(ctx, condStack, "CREATE_TABLE", tableName, stmt);

                        // Register columns (e.g. "CREATE TABLE #Staging (...)") so a later
                        // reference to this table in the same body can resolve them - see
                        // WalkContext.TryGetColumns.
                        var cols = cts2.Definition?.ColumnDefinitions
                            .Select(c => c.ColumnIdentifier.Value)
                            .ToList() ?? new List<string>();
                        ctx.RegisterTransientTable(tableName, cols);
                    }
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

            // Reads hiding in scalar/EXISTS/IN subqueries (WHERE, SELECT list, function
            // args) the per-statement handlers above don't walk. Control-flow containers
            // are skipped here - their bodies recurse, and their own predicates are
            // captured in the IF/WHILE cases.
            if (stmt is not (BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement))
                EmitSubqueryReads(stmt, ctx, condStack, stmt, cteNames, cteBaseTables);

            // A CTE body's own WHERE, emitted *after* the statement it belongs to so the
            // statement keeps the lower step ordinal. Step ids are positional
            // ("<object>#step<N>"), so emitting these first silently renumbered every
            // later step - the community-edge-cases gate for recursive-cte caught it by
            // reporting the expected READS_FROM on #step0 arriving on #step2 instead.
            EmitCteFilterSteps(ctx, condStack, stmt, cteNames, cteBaseTables);

            // Same idea, for a derived table's own WHERE ("FROM (SELECT ... WHERE X)
            // alias" in a SELECT/UPDATE/DELETE's FROM) - a QueryDerivedTable is a
            // different AST node from the ScalarSubquery that ExtractFilterColumnsCore's
            // nested-subquery handling covers (EXISTS/IN/scalar comparison), so it was
            // never reached: "X" belongs to the derived table's own base table, not to
            // the outer alias, and was silently dropped entirely. Emitted after the
            // statement for the same step-ordinal reason as EmitCteFilterSteps above.
            EmitDerivedTableFilterSteps(ctx, condStack, stmt, cteNames, cteBaseTables);
        }
    }


    /// <summary>
    /// Subconsultas del predicado de un IF/WHILE ("IF EXISTS(SELECT 1 FROM t WHERE c=@x)").
    /// WalkIf solo registraba el TEXTO de la condicion y recorria las ramas, asi que ni la
    /// tabla ni las columnas del predicado llegaban al grafo: el control de flujo no es una
    /// sentencia DML y ningun camino bajaba ahi.
    ///
    /// Es el mismo mecanismo de resolucion por scope que ya usan WHERE y la lista SELECT,
    /// aplicado al scope que faltaba. Y es el caso mas seguro de los tres: la subconsulta de
    /// un predicado de control de flujo trae SU PROPIO FROM y no hay scope exterior con el
    /// que confundirse, asi que la atribucion es inequivoca.
    ///
    /// Medido sobre el corpus DNN: 10 ciegas viven en IF EXISTS.
    /// </summary>
    private static void ProcesarSubconsultasDePredicado(BooleanExpression? predicado, TSqlStatement stmt, List<Condition> condStack, WalkContext ctx, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        if (predicado == null)
            return;

        var anidadas = new NestedSubqueryCollector();
        predicado.Accept(anidadas);
        var sinScopeExterior = new List<(string Alias, string Table)>();

        foreach (var nqs in anidadas.Subqueries)
        {
            if (nqs.FromClause == null)
                continue;
            var propias = CollectTableRefs(nqs.FromClause, cteNames, cteBaseTables);
            if (propias.Count == 0)
                continue;

            var colector = new ScopedColumnCollector();
            nqs.Accept(colector);
            var resueltas = ResolveScopedColumns(colector.Refs, propias, sinScopeExterior, tn => ResolveAllColumns(tn, ctx));

            AddLink(ctx, condStack, "SELECT", propias[0].Table, stmt, cteNames, cteBaseTables,
                    extraReads: resueltas, detail: "predicado");
        }
    }

    private static void WalkIf(IfStatement ifs, List<Condition> condStack, WalkContext ctx, int depth, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        ctx.DecisionCount++;
        var ifText = SqlText.Truncate(SqlText.Generate(ifs.Predicate), 140);


        condStack.Add(new Condition("IF", ifText, depth, ifs.StartLine));
        try
        {
            WalkSingleOrBlock(ifs.ThenStatement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
        }
        finally
        {
            condStack.RemoveAt(condStack.Count - 1);
        }

        if (ifs.ElseStatement == null)
            return;

        // ELSE / ELSE IF: condition is "NOT(outer IF)". A chained ELSE IF is
        // itself an IfStatement, so WalkSingleOrBlock recurses into WalkIf,
        // which then pushes its own ("IF", <elseif predicate>, ...) on top -
        // giving each rung of the ladder both its negated-parent context and
        // its own positive condition.
        condStack.Add(new Condition("IF_ELSE", $"NOT ({ifText})", depth, ifs.StartLine));
        try
        {
            WalkSingleOrBlock(ifs.ElseStatement, condStack, ctx, depth + 1, cteNames, cteBaseTables);
        }
        finally
        {
            condStack.RemoveAt(condStack.Count - 1);
        }
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
        var ctes = GetStatementCtes(stmt);

        if (ctes == null)
            return;

        foreach (var cte in ctes.CommonTableExpressions)
            cteNames.Add(cte.ExpressionName.Value);

        foreach (var cte in ctes.CommonTableExpressions)
        {
            // A CTE body can be a single QuerySpecification OR a set operation
            // (BinaryQueryExpression: UNION/UNION ALL of an anchor + recursive member,
            // or any unioned CTE). Collect base tables from every branch so recursive
            // and unioned CTEs resolve instead of contributing nothing. Drop the CTE's
            // own name (the recursive member references itself) to avoid a phantom self-ref.
            var refs = CollectQueryExprTableRefs(cte.QueryExpression, cteNames, cteBaseTables);
            refs.RemoveAll(r => string.Equals(r.Table, cte.ExpressionName.Value, StringComparison.OrdinalIgnoreCase));
            cteBaseTables[cte.ExpressionName.Value] = refs;
        }
    }

    /// <summary>
    /// Base table refs from every QuerySpecification inside a query expression,
    /// recursing through BinaryQueryExpression (UNION/EXCEPT/INTERSECT) and
    /// parenthesized queries - so a recursive or unioned CTE body resolves to all the
    /// real tables its branches read, not just the first one.
    /// </summary>
    private static List<(string Alias, string Table)> CollectQueryExprTableRefs(QueryExpression qe, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var result = new List<(string Alias, string Table)>();
        void Visit(QueryExpression? q)
        {
            switch (q)
            {
                case QuerySpecification { FromClause: not null } qs:
                    foreach (var r in CollectTableRefs(qs.FromClause, cteNames, cteBaseTables))
                        // Dedup by TABLE (anchor + recursive member usually read the same
                        // base table under different aliases - "Employees" vs "e"). Keeping
                        // both would make the CTE look like a 2-table FROM and an outer
                        // query's unqualified columns would be dropped as ambiguous.
                        if (!result.Any(x => string.Equals(x.Table, r.Table, StringComparison.OrdinalIgnoreCase)))
                            result.Add(r);
                    break;
                case BinaryQueryExpression bqe:
                    Visit(bqe.FirstQueryExpression);
                    Visit(bqe.SecondQueryExpression);
                    break;
                case QueryParenthesisExpression qpe:
                    Visit(qpe.QueryExpression);
                    break;
            }
        }
        Visit(qe);
        return result;
    }

    private static WithCtesAndXmlNamespaces? GetCtes(TSqlStatement stmt) =>
        stmt.GetType().GetProperty("WithCtesAndXmlNamespaces")?.GetValue(stmt) as WithCtesAndXmlNamespaces;

    /// <summary>Shared "does this statement have a WITH ... AS (...) clause" lookup, used by both CollectCteNames and EmitCteFilterSteps.</summary>
    private static WithCtesAndXmlNamespaces? GetStatementCtes(TSqlStatement stmt) => stmt switch
    {
        SelectStatement sel => sel.WithCtesAndXmlNamespaces,
        InsertStatement or UpdateStatement or DeleteStatement or MergeStatement => GetCtes(stmt),
        _ => null,
    };

    /// <summary>
    /// Every QuerySpecification branch reachable from a query expression, descending
    /// through UNION/EXCEPT/INTERSECT (BinaryQueryExpression) and parenthesized
    /// queries - same traversal as QueryFromClauses, but yields the QuerySpecification
    /// itself (not just its FromClause) so callers can also reach its WhereClause.
    /// </summary>
    private static IEnumerable<QuerySpecification> FlattenQuerySpecifications(QueryExpression? qe)
    {
        switch (qe)
        {
            case QuerySpecification qs:
                yield return qs;
                break;
            case BinaryQueryExpression bqe:
                foreach (var q in FlattenQuerySpecifications(bqe.FirstQueryExpression)) yield return q;
                foreach (var q in FlattenQuerySpecifications(bqe.SecondQueryExpression)) yield return q;
                break;
            case QueryParenthesisExpression qpe:
                foreach (var q in FlattenQuerySpecifications(qpe.QueryExpression)) yield return q;
                break;
        }
    }

    /// <summary>
    /// Aliases under which <paramref name="cteName"/> is joined back to itself inside
    /// a FROM clause (recursive CTE member, e.g. "... JOIN r ON t.Padre = r.Id" inside
    /// the definition of "r" itself). Walks JOIN structure the same way
    /// CollectJoinExpressions does. Used only to re-attach the alias for filter-column
    /// resolution - CollectTableRefs already substitutes the self-reference with the
    /// CTE's base tables (for read tracking) but drops the alias itself, so a WHERE
    /// like "r.Level &lt; 5" would otherwise be unresolvable.
    /// </summary>
    private static void CollectSelfRefAliases(TableReference tref, string cteName, List<string> aliases)
    {
        switch (tref)
        {
            case NamedTableReference ntr when ntr.SchemaObject.Identifiers.Count == 1
                    && string.Equals(ntr.SchemaObject.BaseIdentifier.Value, cteName, StringComparison.OrdinalIgnoreCase):
                aliases.Add(ntr.Alias?.Value ?? ntr.SchemaObject.BaseIdentifier.Value);
                break;
            case QualifiedJoin qj:
                CollectSelfRefAliases(qj.FirstTableReference, cteName, aliases);
                CollectSelfRefAliases(qj.SecondTableReference, cteName, aliases);
                break;
            case UnqualifiedJoin uqj:
                CollectSelfRefAliases(uqj.FirstTableReference, cteName, aliases);
                CollectSelfRefAliases(uqj.SecondTableReference, cteName, aliases);
                break;
        }
    }

    /// <summary>
    /// Emits one extra "SELECT" step per branch of each CTE defined on this statement,
    /// carrying just that branch's own WHERE (+ JOIN ON) filter columns/text.
    /// Previously silently dropped: CollectCteNames only resolves a CTE to its base
    /// tables for table-REFERENCE purposes (so "FROM c" elsewhere expands to c's real
    /// tables); it never walked the CTE body's own WhereClause, so
    /// "WITH c AS (SELECT ... WHERE X) SELECT ... FROM c" produced zero FILTERS_ON/
    /// BusinessRule for X - and a UNION'd or recursive CTE body lost every branch's
    /// WHERE, including a recursive CTE's stop condition.
    ///
    /// Runs exactly once per statement (called right after CollectCteNames, alongside
    /// it, not from inside the branch-handling switch below) - so a CTE referenced
    /// from several places later in the same statement (or several times across a
    /// UNION) still only contributes its own filter once. It only ever walks the CTE's
    /// OWN QueryExpression, never re-enters a *referenced* CTE's body, so there is no
    /// risk of unbounded recursion through a chain of CTEs referencing each other.
    ///
    /// Recursive CTEs: the recursive member's WHERE often references the CTE's own
    /// alias directly (the stop condition, e.g. "WHERE r.Level &lt; 5") rather than a
    /// real base table/column. That predicate is still genuine domain logic - dropping
    /// it silently (as before) would erase the "when does the recursion stop" rule,
    /// which is exactly the fact that matters most for a recursive CTE. Rather than
    /// leaving it unattributed OR (worse) silently mis-attributing it, "r" is mapped to
    /// the CTE's own resolved base table (already computed by CollectCteNames, run just
    /// before this): a reasoned, explicit choice - the stop condition genuinely
    /// constrains how far the base table's rows get walked, even though "Level" itself
    /// is a computed/aliased column rather than a literal column of that table.
    /// </summary>
    private static void EmitCteFilterSteps(WalkContext ctx, List<Condition> condStack, TSqlStatement stmt, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var ctes = GetStatementCtes(stmt);
        if (ctes == null)
            return;

        foreach (var cte in ctes.CommonTableExpressions)
        {
            var cteName = cte.ExpressionName.Value;
            if (!cteBaseTables.TryGetValue(cteName, out var cteBases) || cteBases.Count == 0)
                continue; // unresolvable CTE body - nothing to attribute a filter to

            foreach (var qs in FlattenQuerySpecifications(cte.QueryExpression))
            {
                if (qs.FromClause == null)
                    continue;

                var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);

                var selfAliases = new List<string>();
                foreach (var tref in qs.FromClause.TableReferences)
                    CollectSelfRefAliases(tref, cteName, selfAliases);
                foreach (var alias in selfAliases.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!tableRefs.Any(t => string.Equals(t.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                        tableRefs.Add((alias, cteBases[0].Table));

                if (tableRefs.Count == 0)
                    continue;

                var (filterColumns, _, filterOpKinds, filterText, filterKind) =
                    ExtractFilterColumnsCore(qs.WhereClause, qs.FromClause.TableReferences, tableRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));

                if (filterColumns.Count == 0)
                    continue; // nothing this branch contributes - don't manufacture an empty step

                // Empty target on purpose. The statement that consumes the CTE already
                // emits READS_FROM for its base tables - that is what cteBaseTables is
                // for. Naming a target here re-declares the same read once per CTE
                // branch: the community-edge-cases gate for recursive-cte caught exactly
                // that, READS_FROM for dbo.Employees going from 1 to 3. This step exists
                // only to carry the branch's own WHERE, which was dropped entirely.
                AddLink(ctx, condStack, "SELECT", "", stmt, cteNames, cteBaseTables,
                    filterColumnsOverride: filterColumns, filterOpKindsOverride: filterOpKinds,
                    filterTextOverride: filterText, filterKindOverride: filterKind);
            }
        }
    }

    /// <summary>
    /// Finds every QueryDerivedTable ("(SELECT ...) alias") directly reachable from a
    /// FROM-clause table reference, recursing through JOIN/PIVOT/UNPIVOT wrappers the
    /// same way CollectTableRefsInto does. Does NOT recurse into a found derived
    /// table's own FROM clause - EmitDerivedTableFilterSteps below does that itself via
    /// FlattenQuerySpecifications on each derived table's own QueryExpression, one
    /// nesting level at a time.
    /// </summary>
    private static void FindDerivedTables(TableReference tref, List<QueryDerivedTable> found)
    {
        switch (tref)
        {
            case QueryDerivedTable qdt:
                found.Add(qdt);
                break;
            case QualifiedJoin qj:
                FindDerivedTables(qj.FirstTableReference, found);
                FindDerivedTables(qj.SecondTableReference, found);
                break;
            case UnqualifiedJoin uqj:
                FindDerivedTables(uqj.FirstTableReference, found);
                FindDerivedTables(uqj.SecondTableReference, found);
                break;
            case PivotedTableReference pvt:
                FindDerivedTables(pvt.TableReference, found);
                break;
            case UnpivotedTableReference unpvt:
                FindDerivedTables(unpvt.TableReference, found);
                break;
        }
    }

    /// <summary>
    /// Emits one extra "SELECT" step per WHERE-bearing branch of every derived table
    /// ("FROM (SELECT ... WHERE X) alias") reachable from this statement's own FROM
    /// clause(s) - mirrors <see cref="EmitCteFilterSteps"/>, but for a QueryDerivedTable
    /// instead of a CTE (a different AST node, not walked by ExtractFilterColumnsCore's
    /// EXISTS/IN/scalar-subquery handling, which only recurses through ScalarSubquery).
    ///
    /// Resolved against the derived table's OWN FROM tables (CollectTableRefs on its own
    /// QuerySpecification), never against the outer query's tables - a derived table's
    /// WHERE is evaluated before the outer query ever sees its rows, so "X" always
    /// belongs to the derived table's own source, never to the outer alias.
    /// </summary>
    private static void EmitDerivedTableFilterSteps(WalkContext ctx, List<Condition> condStack, TSqlStatement stmt, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        List<FromClause?> fromClauses = stmt switch
        {
            SelectStatement { QueryExpression: { } qe } => FlattenQuerySpecifications(qe).Select(q => (FromClause?)q.FromClause).ToList(),
            UpdateStatement upd => new List<FromClause?> { upd.UpdateSpecification?.FromClause },
            DeleteStatement del => new List<FromClause?> { del.DeleteSpecification?.FromClause },
            _ => new List<FromClause?>(),
        };

        foreach (var fromClause in fromClauses)
        {
            if (fromClause == null)
                continue;

            var derivedTables = new List<QueryDerivedTable>();
            foreach (var tref in fromClause.TableReferences)
                FindDerivedTables(tref, derivedTables);

            foreach (var qdt in derivedTables)
            {
                foreach (var qs in FlattenQuerySpecifications(qdt.QueryExpression))
                {
                    if (qs.WhereClause == null || qs.FromClause == null)
                        continue;

                    var ownTableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
                    if (ownTableRefs.Count == 0)
                        continue;

                    var (filterColumns, _, filterOpKinds, filterText, filterKind) =
                        ExtractFilterColumnsCore(qs.WhereClause, qs.FromClause.TableReferences, ownTableRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));

                    if (filterColumns.Count == 0)
                        continue; // nothing this branch contributes - don't manufacture an empty step

                    // Empty target, same reasoning as EmitCteFilterSteps: the derived
                    // table's own base table already gets its READS_FROM from the
                    // outer statement's normal FROM-flattening (CollectTableRefsInto's
                    // QueryDerivedTable case) - this step exists only to carry the
                    // WHERE that would otherwise be dropped entirely.
                    AddLink(ctx, condStack, "SELECT", "", stmt, cteNames, cteBaseTables,
                        filterColumnsOverride: filterColumns, filterOpKindsOverride: filterOpKinds,
                        filterTextOverride: filterText, filterKindOverride: filterKind);
                }
            }
        }
    }

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

    /// <summary>
    /// Column(s) an ALTER TABLE names directly, read off the typed AST (not regex):
    /// ALTER COLUMN's own ColumnIdentifier, or each dropped column from a
    /// "DROP COLUMN A, B" list (TableElementType.Column - skips DROP CONSTRAINT/INDEX/
    /// PERIOD entries mixed into the same statement). Passed through to AddLink's
    /// "columns" param so GraphExporter links the step to the affected :Column
    /// node(s) (WRITES_COLUMN) the same way it does for INSERT/UPDATE - letting an
    /// impact query find a dropped/altered column's readers without a special case.
    /// Empty for ADD COLUMN, RENAME (sp_rename - tracked only as a generic EXEC, see
    /// ExecTarget) and other ALTER TABLE subtypes.
    /// </summary>
    private static List<string> AlterColumns(AlterTableStatement alt) => alt switch
    {
        AlterTableAlterColumnStatement acs when acs.ColumnIdentifier != null =>
            new List<string> { acs.ColumnIdentifier.Value },
        AlterTableDropTableElementStatement dts =>
            dts.AlterTableDropTableElements
                .Where(e => e.TableElementType == TableElementType.Column && e.Name != null)
                .Select(e => e.Name.Value)
                .ToList(),
        _ => new List<string>(),
    };

    private static void AddLink(
        WalkContext ctx,
        List<Condition> condStack,
        string consequenceType,
        string target,
        TSqlStatement stmt,
        HashSet<string>? cteNames = null,
        Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables = null,
        IReadOnlyList<string>? dynamicSqlVars = null,
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<ColumnDerivation>? columnLineage = null,
        IReadOnlyList<TableColumnRef>? extraReads = null,
        IReadOnlyList<TableColumnRef>? filterColumnsOverride = null,
        IReadOnlyList<string>? filterOpKindsOverride = null,
        string? filterTextOverride = null,
        string? filterKindOverride = null,
        string detail = "",
        string dynamicSqlText = "",
        bool selectStar = false)
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

        // FilterColumns: columns from this step's own WHERE/JOIN predicates, resolved
        // against the same FROM-clause table refs the step's other column tracking
        // uses (callers that already computed their own filter columns - e.g. MERGE's
        // ON clause isn't a WhereClause - pass filterColumnsOverride directly instead).
        var filterColumns = filterColumnsOverride;
        var nestedTableRefs = new List<(string Alias, string Table)>();
        List<string> filterOpKinds = (filterOpKindsOverride ?? Array.Empty<string>()).ToList();
        string filterText = filterTextOverride ?? "";
        string filterKind = filterKindOverride ?? "";
        if (filterColumns == null && cteNames != null && cteBaseTables != null)
        {
            // UPDATE/DELETE without an explicit FROM clause has no FromClause to walk -
            // the target itself is the implicit single source table, so a bare
            // "WHERE Status = 'X'" still resolves (tableRefs.Count == 1, same rule
            // SplitColumnsByTable already uses for unqualified columns).
            List<(string Alias, string Table)> currentTableRefs = stmt switch
            {
                SelectStatement { QueryExpression: QuerySpecification { FromClause: not null } qs } => CollectTableRefs(qs.FromClause, cteNames, cteBaseTables),
                UpdateStatement { UpdateSpecification.FromClause: not null } upd => CollectTableRefs(upd.UpdateSpecification!.FromClause, cteNames, cteBaseTables),
                UpdateStatement upd2 when TargetName(upd2.UpdateSpecification?.Target, cteNames) is { Length: > 0 } tn => new List<(string Alias, string Table)> { ("", tn) },
                DeleteStatement { DeleteSpecification.FromClause: not null } del => CollectTableRefs(del.DeleteSpecification!.FromClause, cteNames, cteBaseTables),
                DeleteStatement del2 when TargetName(del2.DeleteSpecification?.Target, cteNames) is { Length: > 0 } tn => new List<(string Alias, string Table)> { ("", tn) },
                _ => new List<(string Alias, string Table)>(),
            };
            (filterColumns, nestedTableRefs, filterOpKinds, filterText, filterKind) = ExtractFilterColumns(stmt, currentTableRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));
        }

        // A nested EXISTS/IN/scalar-comparison subquery's own table (e.g.
        // "EXISTS (SELECT 1 FROM T2 WHERE ...)") isn't read via the step's normal
        // INSERT/UPDATE/SELECT target tracking, only surfaces through FilterColumns -
        // mirror it into extraReads too so it gets a real READS_FROM, not just FILTERS_ON.
        var mergedExtraReads = extraReads;
        if (nestedTableRefs.Count > 0)
        {
            var merged = (extraReads ?? Array.Empty<TableColumnRef>()).ToList();
            foreach (var (_, table) in nestedTableRefs)
                if (!merged.Any(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(new TableColumnRef(table, Array.Empty<string>()));
            mergedExtraReads = merged;
        }

        ctx.FlowLinks.Add(new FlowLinkInfo(
            condType, condText, consequenceType, target,
            condStack.Count, stmt.StartLine, dynamicSqlVars, path, keys, columns, columnLineage, usedVariables, mergedExtraReads,
            FilterColumns: filterColumns,
            FilterOpKinds: filterOpKinds,
            Detail: detail,
            DynamicSqlText: dynamicSqlText,
            SelectStar: selectStar,
            FilterText: filterText,
            FilterKind: filterKind
        ));
    }

    /// <summary>
    /// Pulls the columns referenced in a step's own WHERE clause and JOIN ON
    /// predicates (not its SELECT/SET list), resolved against the same FROM-clause
    /// table refs used elsewhere for this step - i.e. "what decided which rows this
    /// step touched", as opposed to Columns/ColumnLineage ("what got read/written").
    /// Only SELECT/UPDATE/DELETE have a WhereClause; other statement kinds (INSERT,
    /// MERGE's ON clause, EXEC, ...) simply yield no filter columns here.
    /// </summary>
    private static (List<TableColumnRef> FilterColumns, List<(string Alias, string Table)> NestedTableRefs, List<string> FilterOpKinds, string FilterText, string FilterKind) ExtractFilterColumns(
        TSqlStatement stmt, List<(string Alias, string Table)> tableRefs, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables,
        Func<string, List<string>?>? columnsOf = null)
    {
        WhereClause? whereClause = stmt switch
        {
            SelectStatement { QueryExpression: QuerySpecification qs } => qs.WhereClause,
            UpdateStatement upd => upd.UpdateSpecification?.WhereClause,
            DeleteStatement del => del.DeleteSpecification?.WhereClause,
            _ => null,
        };

        var tRefs = (stmt switch
        {
            SelectStatement { QueryExpression: QuerySpecification qs } => qs.FromClause?.TableReferences,
            UpdateStatement upd => upd.UpdateSpecification?.FromClause?.TableReferences,
            DeleteStatement del => del.DeleteSpecification?.FromClause?.TableReferences,
            _ => null,
        }) ?? new List<TableReference>();

        return ExtractFilterColumnsCore(whereClause, tRefs, tableRefs, cteNames, cteBaseTables, columnsOf);
    }

    /// <summary>
    /// Core of <see cref="ExtractFilterColumns"/>, usable directly with a WhereClause +
    /// FROM table references instead of re-deriving them from a top-level
    /// TSqlStatement - needed for "SET @v = (SELECT ... WHERE ...)" scalar subqueries,
    /// which have their own QuerySpecification but aren't a TSqlStatement of their own.
    ///
    /// Also resolves columns from any EXISTS/IN/scalar-comparison subquery nested
    /// inside the WHERE/JOIN-ON predicates: QualifiedColumnCollector already descends
    /// into them and collects their column refs (e.g. "sisg.StockGroupID" inside an
    /// "EXISTS (SELECT 1 FROM ... AS sisg WHERE ...)"), but until now they were
    /// silently dropped by SplitColumnsByTable because "sisg" wasn't in the outer
    /// tableRefs. NestedTableRefs (each nested subquery's own FROM tables) is merged
    /// in before resolving, and returned separately so the caller (AddLink) can also
    /// add a READS_FROM for those tables, not just FILTERS_ON.
    /// </summary>
    private static (List<TableColumnRef> FilterColumns, List<(string Alias, string Table)> NestedTableRefs, List<string> FilterOpKinds, string FilterText, string FilterKind) ExtractFilterColumnsCore(
        WhereClause? whereClause, IList<TableReference> fromTableReferences, List<(string Alias, string Table)> tableRefs,
        HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables,
        Func<string, List<string>?>? columnsOf = null)
    {
        var filterRefs = new List<(string? Qualifier, string Column)>();
        var nestedTableRefs = new List<(string Alias, string Table)>();
        var opKinds = new SortedSet<string>(StringComparer.Ordinal);
        // Per-nested-subquery column resolutions (own scope first, see
        // ResolveScopedColumns) - kept separate from filterRefs/mergedTableRefs so an
        // unqualified name shared by an outer and an inner table (e.g. "ModuleDefID" in
        // both dbo.Permission and dbo.ModuleDefinitions) resolves in EACH scope on its
        // own instead of colliding into one "ambiguous, drop both" lookup. Root cause of
        // 58/140 blind column refs measured on the DNN corpus (eval/column-recall/
        // blind-refs.md, causa #1) - see ScopedColumnCollector/ResolveScopedColumns.
        var nestedResolved = new List<TableColumnRef>();

        void CollectFrom(TSqlFragment? fragment)
        {
            if (fragment == null)
                return;

            // Outer-scope refs only: ScopedColumnCollector stops at a nested
            // ScalarSubquery's boundary, so a column that only exists inside the
            // subquery never reaches this list (and can't collide with the outer
            // resolution below).
            var collector = new ScopedColumnCollector();
            fragment.Accept(collector);
            filterRefs.AddRange(collector.Refs);

            // Operator structure (AND/OR/comparison/LIKE/IN/...) behind the predicate -
            // the queryable complement to the columns it touches.
            foreach (var op in OperatorClassifier.Classify(fragment))
                opKinds.Add(op);

            var nested = new NestedSubqueryCollector();
            fragment.Accept(nested);
            foreach (var nestedQs in nested.Subqueries)
            {
                if (nestedQs.FromClause == null)
                    continue;
                var ownTableRefs = CollectTableRefs(nestedQs.FromClause, cteNames, cteBaseTables);
                foreach (var nestedRef in ownTableRefs)
                    if (!nestedTableRefs.Contains(nestedRef))
                        nestedTableRefs.Add(nestedRef);

                // The subquery's own SELECT/WHERE/etc columns, collected and resolved in
                // its own FROM scope (falling back to the outer tableRefs only for a
                // correlated reference) - NOT merged into filterRefs above.
                var ownCollector = new ScopedColumnCollector();
                nestedQs.Accept(ownCollector);
                nestedResolved.AddRange(ResolveScopedColumns(ownCollector.Refs, ownTableRefs, tableRefs, columnsOf));
            }
        }

        CollectFrom(whereClause?.SearchCondition);

        var joinConditions = new List<BooleanExpression>();
        CollectJoinExpressions(fromTableReferences, joinConditions);
        foreach (var expr in joinConditions)
            CollectFrom(expr);

        // filterRefs is outer-scope-only now (ScopedColumnCollector stops at a nested
        // subquery's boundary), so nestedTableRefs can never change which TABLE an outer
        // ref resolves to. It is still folded into the candidate pool here, though: when
        // a nested subquery resolves to the SAME real table as the outer query (common
        // for a CTE referenced both in FROM and inside a correlated subquery, e.g. "FROM
        // ArchiveBox ... WHERE RowNumber BETWEEN (SELECT ... FROM ArchiveBox WHERE ...)"),
        // padding the pool past a single entry routes an unqualified outer ref through
        // ResolveUnqualified's schema-checked path instead of the count==1 shortcut's
        // unconditional guess - which matters because "RowNumber" there is a CTE-computed
        // alias, not a real column of the base table, and the count==1 shortcut doesn't
        // consult the schema catalog at all. Measured: dropping this padding introduced 4
        // new false positives on the DNN corpus precision gate (all this exact shape).
        var outerResolutionPool = nestedTableRefs.Count > 0 ? tableRefs.Concat(nestedTableRefs).ToList() : tableRefs;
        var (primaryCols, extras) = SplitColumnsByTable(filterRefs, outerResolutionPool, columnsOf);

        var result = new List<TableColumnRef>();
        if (primaryCols.Count > 0 && tableRefs.Count > 0)
            result.Add(new TableColumnRef(tableRefs[0].Table, primaryCols));
        result.AddRange(extras);

        // Merge the nested subqueries' own resolutions in, combining with any table that
        // already has an entry (e.g. a correlated ref that landed back on tableRefs[0] or
        // on an extras table already touched by the outer predicate) instead of adding a
        // second TableColumnRef for the same table.
        foreach (var nr in nestedResolved)
        {
            var idx = result.FindIndex(r => string.Equals(r.Table, nr.Table, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                result.Add(nr);
                continue;
            }
            var merged = result[idx].Columns.Concat(nr.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result[idx] = new TableColumnRef(result[idx].Table, merged);
        }

        // WHERE-only text/classification - deliberately excludes the JOIN ON
        // predicates folded into filterRefs/opKinds above, so a step's :BusinessRule
        // (see GraphExporter) reflects only its actual WHERE condition, not the
        // join's key-matching predicate. A step with a JOIN but no WHERE therefore
        // never manufactures a rule.
        var filterText = whereClause?.SearchCondition != null
            ? SqlText.Truncate(SqlText.Generate(whereClause.SearchCondition), 300)
            : "";
        var filterKind = whereClause?.SearchCondition != null
            ? FilterRuleClassifier.Classify(whereClause.SearchCondition)
            : "";

        return (result, nestedTableRefs, opKinds.ToList(), filterText, filterKind);
    }

    /// <summary>
    /// Collects every ScalarSubquery in a fragment - ScriptDom's shared wrapper for
    /// "(SELECT ...)", used identically for a plain scalar comparison
    /// ("col = (SELECT ...)"), the contents of EXISTS(...)/IN(...), and a parenthesized
    /// SET assignment. Recurses naturally (doesn't stop descending once one is found),
    /// so a subquery nested inside another subquery is also collected.
    ///
    /// Ademas de ScalarSubquery, tambien recoge el cuerpo de una tabla derivada
    /// ("(SELECT ...) alias" en un FROM, QueryDerivedTable) y el de un CTE ("WITH x AS
    /// (SELECT ...)", CommonTableExpression). Ninguno de los dos ES un ScalarSubquery,
    /// asi que quedaban fuera aunque EmitSubqueryReads visita el fragmento entero de la
    /// sentencia: sus columnas propias (lista SELECT, JOIN...ON) no tenian scope donde
    /// resolverse y se descartaban en silencio. FlattenQuerySpecifications cubre el caso
    /// UNION en cualquiera de los dos.
    ///
    /// Casos reales DNN: dbo.CoreMessaging_ConvertLegacyMessages, cursor sobre "WITH
    /// messageItems AS (SELECT [MessageID],[PortalID],... FROM dbo.Messaging_Messages)
    /// SELECT * FROM messageItems ..." - las columnas del propio SELECT del CTE (6 en
    /// total) no llegaban a ningun sitio. Y dbo.Journal_ListForGroup, con una tabla
    /// derivada cuyo JOIN interno "...ON js.JournalId = j.JournalId" tampoco resolvia.
    ///
    /// El seguimiento de tabla derivada y de CTE es opcional (por defecto los dos
    /// apagados, para no tocar el comportamiento de los llamadores que ya existian:
    /// ProcesarSubconsultasDePredicado y ScopedRefsFromSelectList solo necesitan
    /// ScalarSubquery). Solo EmitSubqueryReads los activa, y el de CTE encima SOLO para
    /// un DECLARE CURSOR - el unico sitio donde el WITH queda genuinamente ciego (ver el
    /// porque alli). Fuera de un cursor ya hay cobertura (WHERE via EmitCteFilterSteps,
    /// columnas propias via el flattening normal de la sentencia que consume el CTE), y
    /// seguirlo ahi tambien solo producia pasos redundantes: medido con
    /// CteUnionFilterTests.Governs_IsUnchangedByFilterExtraction, que paso de 2 a 3
    /// GOVERNS. La tabla derivada no tiene ese problema, así que EmitSubqueryReads la
    /// sigue siempre.
    /// </summary>
    private sealed class NestedSubqueryCollector : TSqlFragmentVisitor
    {
        private readonly bool _followDerivedTable;
        private readonly bool _followCte;

        public NestedSubqueryCollector(bool followDerivedTable = false, bool followCte = false)
        {
            _followDerivedTable = followDerivedTable;
            _followCte = followCte;
        }

        public List<QuerySpecification> Subqueries { get; } = new();

        public override void Visit(ScalarSubquery node)
        {
            if (node.QueryExpression is QuerySpecification qs)
                Subqueries.Add(qs);
        }

        public override void Visit(QueryDerivedTable node)
        {
            if (!_followDerivedTable)
                return;
            foreach (var qs in FlattenQuerySpecifications(node.QueryExpression))
                Subqueries.Add(qs);
        }

        public override void Visit(CommonTableExpression node)
        {
            if (!_followCte)
                return;
            foreach (var qs in FlattenQuerySpecifications(node.QueryExpression))
                Subqueries.Add(qs);
        }
    }

    /// <summary>
    /// Same (qualifier, column) collection as <see cref="QualifiedColumnCollector"/>, but
    /// stops at a nested ScalarSubquery's boundary instead of descending into it - unlike
    /// <c>Visit</c>, overriding <c>ExplicitVisit</c> (and not calling into the subquery's
    /// children) actually prevents ScriptDom's default traversal from walking in.
    ///
    /// Used by ExtractFilterColumnsCore to collect a predicate's OWN column refs (outer
    /// WHERE/JOIN-ON, or one specific nested subquery's own SELECT/WHERE) without also
    /// picking up refs that belong to a *different* scope. Applied twice: once at the
    /// outer predicate (root call), and once per nested subquery found by
    /// NestedSubqueryCollector (so each subquery's own refs are collected and resolved
    /// against its own FROM tables, not flattened together with the outer query's).
    /// </summary>
    private sealed class ScopedColumnCollector : TSqlFragmentVisitor
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

        // Mirrors QualifiedColumnCollector.Visit(FunctionCall) - see there for why.
        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is not MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } ids })
                return;
            if (!IsXmlAccessorMethod(node.FunctionName?.Value))
                return;
            var column = ids[^1].Value;
            var qualifier = ids.Count > 1 ? ids[^2].Value : null;
            Refs.Add((qualifier, column));
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
            // Boundary: don't descend. The subquery's own refs are collected separately
            // by a fresh ScopedColumnCollector rooted at its own QuerySpecification.
        }

        // Mismo corte para una tabla derivada anidada dentro de otra ("(SELECT ... FROM
        // (SELECT ...) inner) outer"): trae su propio FROM/scope. Sin este boundary,
        // ScopedColumnCollector mezclaria sus columnas con las del scope que la contiene,
        // violando la regla de ambito. NestedSubqueryCollector ya la recoge aparte (via
        // su propio Visit(QueryDerivedTable)) para resolverla con su propia
        // ResolveScopedColumns. Un CTE no necesita el mismo corte: T-SQL no permite un
        // WITH anidado dentro de una tabla derivada o de otro cuerpo de CTE.
        public override void ExplicitVisit(QueryDerivedTable node)
        {
        }
    }

    /// <summary>
    /// Resolves a nested subquery's OWN (qualifier, column) refs against its own FROM
    /// tables (innermost-scope-wins, the real SQL binding rule): a QUALIFIED ref may
    /// still resolve to the outer query's tables (a correlated reference, e.g. "EXISTS
    /// (SELECT 1 FROM T2 WHERE T2.X = o.Y)" - "o" is the outer alias), but an
    /// UNQUALIFIED ref that isn't satisfied by the subquery's own scope is dropped
    /// rather than guessed onto the outer table (see the no-fallback comment below).
    ///
    /// Without this scope order, resolving the subquery's refs together with the outer
    /// query's refs against their UNION (the previous behavior) made any unqualified name
    /// that happens to exist in both scopes "ambiguous" by ResolveUnqualified's
    /// deliberately-conservative rule, silently dropping it from BOTH scopes - even though
    /// each occurrence is perfectly resolvable on its own. This is extremely common for FK
    /// id columns (e.g. "ModuleDefID" existing in both an outer permissions table and the
    /// inner table it's filtered against) - see AstWalker root-cause note on
    /// ExtractFilterColumnsCore for the real corpus example that exposed it.
    /// </summary>

    /// <summary>
    /// Columnas de las subconsultas anidadas EN LA LISTA SELECT, resueltas cada una en su
    /// propio scope. El mecanismo (ScopedColumnCollector + ResolveScopedColumns) ya existia,
    /// pero solo se aplicaba en ExtractFilterColumnsCore, es decir solo a clausulas WHERE.
    /// Por eso "WHERE x IN (SELECT ...)" resolvia y
    /// "SELECT (SELECT COUNT(*) FROM h WHERE h.Estado=0) AS N FROM m" no: la columna Estado
    /// se recogia con el colector normal, que desciende a todo, y se resolvia contra las
    /// tablas del scope EXTERIOR, donde no casa con nada y se descartaba por fallo cerrado.
    ///
    /// Medido sobre el corpus DNN: 60 de las 89 ciegas restantes viven en un scope anidado,
    /// y la subconsulta escalar de la lista SELECT es el grupo mayor.
    ///
    /// Resolver en el scope propio, con el exterior solo como respaldo para referencias
    /// correlacionadas, es la regla de ambito del propio T-SQL: no adivina, la implementa.
    /// </summary>
    private static List<TableColumnRef> ScopedRefsFromSelectList(
        QuerySpecification qs,
        List<(string Alias, string Table)> tableRefs,
        HashSet<string> cteNames,
        Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables,
        WalkContext ctx)
    {
        var salida = new List<TableColumnRef>();
        foreach (var el in qs.SelectElements)
        {
            var expr = el switch
            {
                SelectScalarExpression sse => sse.Expression,
                SelectSetVariable ssv => ssv.Expression,
                _ => null,
            };
            if (expr == null)
                continue;

            var anidadas = new NestedSubqueryCollector();
            expr.Accept(anidadas);
            foreach (var nqs in anidadas.Subqueries)
            {
                if (nqs.FromClause == null)
                    continue;
                var propias = CollectTableRefs(nqs.FromClause, cteNames, cteBaseTables);
                if (propias.Count == 0)
                    continue;
                var colector = new ScopedColumnCollector();
                nqs.Accept(colector);
                salida.AddRange(ResolveScopedColumns(colector.Refs, propias, tableRefs, tn => ResolveAllColumns(tn, ctx)));
            }
        }
        return salida;
    }


    /// <summary>Une lecturas por tabla sin duplicar columnas.</summary>
    private static void FusionarLecturas(List<TableColumnRef> destino, List<TableColumnRef> nuevas)
    {
        foreach (var n in nuevas)
        {
            var i = destino.FindIndex(d => string.Equals(d.Table, n.Table, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                destino.Add(n);
            else
                destino[i] = new TableColumnRef(destino[i].Table,
                    destino[i].Columns.Union(n.Columns, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private static List<TableColumnRef> ResolveScopedColumns(
        IEnumerable<(string? Qualifier, string Column)> refs,
        List<(string Alias, string Table)> ownTableRefs,
        List<(string Alias, string Table)> outerTableRefs,
        Func<string, List<string>?>? columnsOf)
    {
        var buckets = new List<(string Table, List<string> Columns, HashSet<string> Seen)>();

        void Add(string table, string column)
        {
            if (table.Length == 0)
                return;
            var idx = buckets.FindIndex(b => string.Equals(b.Table, table, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                buckets.Add((table, new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                idx = buckets.Count - 1;
            }
            if (buckets[idx].Seen.Add(column))
                buckets[idx].Columns.Add(column);
        }

        foreach (var (qualifier, column) in refs)
        {
            if (qualifier != null)
            {
                var i = ownTableRefs.FindIndex(t => string.Equals(t.Alias, qualifier, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { Add(ownTableRefs[i].Table, column); continue; }
                var j = outerTableRefs.FindIndex(t => string.Equals(t.Alias, qualifier, StringComparison.OrdinalIgnoreCase));
                if (j >= 0) Add(outerTableRefs[j].Table, column);
                continue;
            }

            if (ownTableRefs.Count == 1 && ownTableRefs[0].Table.Length > 0)
            {
                // Unlike SplitColumnsByTable's identical single-table shortcut (which never
                // consults the schema catalog, by long-standing design), this one DOES when
                // the catalog knows the table - because the subquery's single FROM table is
                // very often a CTE resolved back to its base table (CollectTableRefs), and
                // the CTE may expose a COMPUTED column (an alias, e.g. "ROW_NUMBER() OVER(...)
                // AS RowNumber") that the base table never had. Guessing that onto the base
                // table is a genuine false positive, not a conservative default - measured on
                // the DNN corpus precision gate (a "RowNumber BETWEEN (SELECT RowNumber ...
                // FROM ArchiveBoxCte WHERE ...) ..." pattern). Schema unknown (null) keeps the
                // old unconditional behavior so schema-less callers/tests are unaffected.
                var knownCols = columnsOf?.Invoke(ownTableRefs[0].Table);
                if (knownCols == null || knownCols.Contains(column, StringComparer.OrdinalIgnoreCase))
                    Add(ownTableRefs[0].Table, column);
                continue;
            }
            if (columnsOf != null && ResolveUnqualified(column, ownTableRefs, columnsOf) is { } hit) { Add(ownTableRefs[hit].Table, column); continue; }

            // Deliberately NOT falling back to outerTableRefs here: an unqualified column
            // that isn't satisfied by the subquery's own scope is not automatically a
            // correlated outer reference - it could just as well be an uncataloged column
            // of the subquery's own table (columnsOf returning null/no-match isn't proof
            // the table lacks it). Guessing it onto the outer table risks a false
            // READS_FROM/FILTERS_ON edge; dropping it keeps the same "don't guess"
            // contract SplitColumnsByTable already uses elsewhere. A real correlated
            // reference is virtually always qualified in practice (e.g. "o.Y"), and that
            // case is already handled above via the qualifier branch.
        }

        return buckets.Select(b => new TableColumnRef(b.Table, b.Columns)).ToList();
    }

    /// <summary>Recursively collects every JOIN's ON predicate from a list of table references (both sides of nested JOINs).</summary>
    private static void CollectJoinExpressions(IList<TableReference> refs, List<BooleanExpression> expressions)
    {
        foreach (var tref in refs)
        {
            if (tref is QualifiedJoin qj)
            {
                if (qj.SearchCondition != null)
                    expressions.Add(qj.SearchCondition);
                CollectJoinExpressions(new[] { qj.FirstTableReference, qj.SecondTableReference }, expressions);
            }
            else if (tref is UnqualifiedJoin uqj)
            {
                CollectJoinExpressions(new[] { uqj.FirstTableReference, uqj.SecondTableReference }, expressions);
            }
        }
    }

    /// <summary>
    /// For an INSERT/UPDATE/DELETE/MERGE with an "OUTPUT ... INTO OutputTable" clause:
    /// records a separate INSERT step writing OutputTable, reading from the
    /// inserted/deleted pseudo-tables (mapped here to the real actionTarget, since
    /// that's the only table whose columns they actually mirror).
    /// </summary>

    /// <summary>
    /// OUTPUT sin INTO: "INSERT ... OUTPUT inserted.MessageId VALUES(...)", que devuelve los
    /// valores al llamador en vez de volcarlos a una tabla. Es la forma mas comun de OUTPUT y
    /// quedaba ENTERA fuera del walker, porque ScriptDom la expone en OutputClause y aqui solo
    /// se leia OutputIntoClause. Caso real: DNN dbo.AddRedirectMessage, cuya referencia a
    /// MessageId era una de las 90 ciegas del corpus.
    ///
    /// No hay lineage de derivacion posible (no existe tabla destino cuyas columnas nombrar),
    /// pero si hay LECTURAS, y su atribucion es inequivoca: inserted/deleted son pseudo-tablas
    /// que reflejan las columnas del objetivo del DML. Por eso este caso NO comparte el riesgo
    /// de precision de ORDER BY ni de las subconsultas, donde hay que decidir a cual de varias
    /// tablas del FROM pertenece la columna.
    ///
    /// Restringido a los calificadores inserted/deleted a proposito: T-SQL exige el prefijo en
    /// OUTPUT (lo unico sin calificar que admite es $action), asi que cualquier otra cosa no es
    /// una columna del objetivo y atribuirsela seria inventar una arista.
    /// </summary>
    private static void ProcessOutputSinInto(IList<SelectElement> selectColumns, string actionTarget, WalkContext ctx, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables, List<Condition> condStack, TSqlStatement stmt)
    {
        if (string.IsNullOrEmpty(actionTarget))
            return;

        var refsSueltas = new List<(string? Qualifier, string Column)>();
        foreach (var element in selectColumns.OfType<SelectScalarExpression>())
        {
            var collectorSuelto = new QualifiedColumnCollector();
            element.Expression.Accept(collectorSuelto);
            refsSueltas.AddRange(collectorSuelto.Refs);
        }

        var columnasObjetivo = refsSueltas
            .Where(r => string.Equals(r.Qualifier, "inserted", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r.Qualifier, "deleted", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Column)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (columnasObjetivo.Count == 0)
            return;

        AddLink(ctx, condStack, "OUTPUT", actionTarget, stmt, cteNames, cteBaseTables,
                extraReads: new List<TableColumnRef> { new(actionTarget, columnasObjetivo) });
    }

    private static void ProcessOutputClause(TSqlFragment? output, string actionTarget, WalkContext ctx, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables, List<Condition> condStack, TSqlStatement stmt)
    {
        // ScriptDom modela OUTPUT en DOS propiedades distintas: OutputIntoClause para
        // "OUTPUT ... INTO tabla" y OutputClause para "OUTPUT ..." a secas. El codigo solo
        // leia la primera, asi que el OUTPUT sin INTO -la forma mas comun- no llegaba
        // siquiera a esta funcion.
        if (output is OutputClause outputSuelto)
        {
            ProcessOutputSinInto(outputSuelto.SelectColumns, actionTarget, ctx, cteNames, cteBaseTables, condStack, stmt);
            return;
        }

        if (output is not OutputIntoClause outputInto)
            return;

        // OUTPUT SIN "INTO" - "INSERT ... OUTPUT inserted.MessageId VALUES(...)", que
        // devuelve los valores al llamador en vez de volcarlos a una tabla. Es la forma mas
        // comun de OUTPUT y quedaba ENTERA fuera: el metodo salia en la guarda de arriba
        // antes de mirar una sola columna. Caso real: DNN dbo.AddRedirectMessage, cuya
        // referencia a MessageId era una de las 90 ciegas del corpus.
        //
        // No hay lineage de derivacion posible (no existe tabla destino cuyas columnas
        // nombrar), pero si hay LECTURAS, y su atribucion es inequivoca: inserted/deleted
        // son pseudo-tablas que reflejan las columnas del objetivo del DML. Por eso este
        // caso no comparte el riesgo de precision de ORDER BY o de las subconsultas, donde
        // hay que decidir a cual de varias tablas del FROM pertenece la columna.
        //
        // Restringido a los calificadores inserted/deleted a proposito: T-SQL exige el
        // prefijo en OUTPUT (lo unico sin calificar que admite es $action), asi que
        // cualquier otra cosa no es una columna del objetivo y atribuirsela seria inventar
        // una arista.
        if (outputInto.IntoTable == null)
        {
            ProcessOutputSinInto(outputInto.SelectColumns, actionTarget, ctx, cteNames, cteBaseTables, condStack, stmt);
            return;
        }

        string outputTargetName;
        if (outputInto.IntoTable is NamedTableReference ntr)
            outputTargetName = IsCte(ntr.SchemaObject, cteNames) ? "" : SqlText.Generate(ntr.SchemaObject);
        else if (outputInto.IntoTable is VariableTableReference vtr)
            outputTargetName = vtr.Variable?.Name ?? "";
        else
            outputTargetName = TargetName(outputInto.IntoTable, cteNames);

        if (string.IsNullOrEmpty(outputTargetName))
            return;

        // inserted/deleted both mirror the action target's columns, so resolve either to it.
        var tableRefs = new List<(string Alias, string Table)> { ("inserted", actionTarget), ("deleted", actionTarget) };
        var selectCols = outputInto.SelectColumns.OfType<SelectScalarExpression>().ToList();

        var allRefs = new List<(string? Qualifier, string Column)>();
        foreach (var element in selectCols)
        {
            var collector = new QualifiedColumnCollector();
            element.Expression.Accept(collector);
            allRefs.AddRange(collector.Refs);
        }
        var (_, extras) = SplitColumnsByTable(allRefs, tableRefs);

        // Column lineage: "OUTPUT inserted.X, deleted.Y INTO Log(A, B)" -> Log.A DERIVES_FROM
        // <target>.X, Log.B DERIVES_FROM <target>.Y (positional, like INSERT ... SELECT).
        // Needs the explicit INTO column list to name the log's target columns.
        var outCols = (outputInto.IntoTableColumns ?? (IList<ColumnReferenceExpression>)Array.Empty<ColumnReferenceExpression>())
            .Select(ColumnName).ToList();
        var outLineage = new List<ColumnDerivation>();
        if (outCols.Count > 0 && outCols.Count == selectCols.Count)
        {
            for (int i = 0; i < outCols.Count; i++)
            {
                var collector = new QualifiedColumnCollector();
                selectCols[i].Expression.Accept(collector);
                if (collector.Refs.Count == 0)
                    continue;   // OUTPUT $action / literal - no column source
                var (primaryCols, exs) = SplitColumnsByTable(collector.Refs, tableRefs, tn => ResolveAllColumns(tn, ctx));
                var exprText = SqlText.Generate(selectCols[i].Expression);
                var exprOps = OperatorClassifier.Classify(selectCols[i].Expression);
                if (primaryCols.Count > 0)
                    outLineage.Add(new ColumnDerivation(outCols[i], tableRefs[0].Table, primaryCols, exprText, exprOps));
                foreach (var ex in exs)
                    outLineage.Add(new ColumnDerivation(outCols[i], ex.Table, ex.Columns, exprText, exprOps));
            }
        }

        AddLink(ctx, condStack, "OUTPUT", outputTargetName, stmt, cteNames, cteBaseTables, columns: outCols, columnLineage: outLineage, extraReads: extras);
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
    /// <summary>
    /// Every FROM clause reachable from a query expression, descending through UNION/
    /// EXCEPT/INTERSECT (BinaryQueryExpression) and parentheses - so a cursor (or any
    /// set-operation query) whose branches each read different tables contributes all
    /// of them, not just the first QuerySpecification.
    /// </summary>
    private static IEnumerable<FromClause> QueryFromClauses(QueryExpression? qe)
    {
        switch (qe)
        {
            case QuerySpecification { FromClause: not null } qs:
                yield return qs.FromClause;
                break;
            case BinaryQueryExpression bqe:
                foreach (var f in QueryFromClauses(bqe.FirstQueryExpression)) yield return f;
                foreach (var f in QueryFromClauses(bqe.SecondQueryExpression)) yield return f;
                break;
            case QueryParenthesisExpression qpe:
                foreach (var f in QueryFromClauses(qpe.QueryExpression)) yield return f;
                break;
        }
    }

    /// <summary>
    /// Emits a SELECT read step for every real table referenced inside a scalar /
    /// EXISTS / IN subquery within <paramref name="fragment"/> (a statement or a
    /// predicate). These live in WHERE clauses, SELECT-list expressions and function
    /// arguments - read lineage the FROM-based handlers never see (e.g. a function
    /// whose body is just `RETURN (SELECT 1 WHERE EXISTS (SELECT ... FROM T))`).
    /// Deduplicated to the owning object downstream, so overlapping with a main FROM
    /// read is harmless.
    /// </summary>
    private static void EmitSubqueryReads(TSqlFragment? fragment, WalkContext ctx, List<Condition> condStack, TSqlStatement stmt, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        if (fragment == null)
            return;

        // Seguir el cuerpo de un CTE aqui esta restringido a un DECLARE CURSOR: es el
        // UNICO sitio donde el WITH queda genuinamente ciego (GetStatementCtes no
        // reconoce DeclareCursorStatement, asi que ni CollectCteNames ni
        // EmitCteFilterSteps lo ven - el WITH cuelga de dcs.CursorDefinition.Select, no
        // del propio stmt). Fuera de un cursor (un SELECT/INSERT/UPDATE/DELETE/MERGE
        // normal con WITH), el cuerpo del CTE YA tiene cobertura: EmitCteFilterSteps
        // cubre su WHERE, y la sentencia que consume el CTE ya cubre sus columnas
        // propias via el flattening normal a traves de cteBaseTables. Medido con el
        // repro de CteUnionFilterTests.Governs_IsUnchangedByFilterExtraction: sin esta
        // restriccion, "WITH c AS (SELECT Id FROM T4 WHERE Borrado=0) SELECT Id FROM c"
        // generaba un tercer paso 100% redundante (Id ya en el paso del SELECT externo,
        // Borrado ya en el paso de EmitCteFilterSteps) que inflaba GOVERNS de 2 a 3.
        // La tabla derivada NO tiene este problema: EmitDerivedTableFilterSteps solo
        // cubre su WHERE, nunca su JOIN...ON, y ninguna sentencia "de fuera" flatten-ea
        // sus columnas propias - por eso QueryDerivedTable se sigue siempre.
        var followCte = stmt is DeclareCursorStatement;

        var collector = new ScalarSubqueryTableCollector(cteNames, cteBaseTables, followCte);
        fragment.Accept(collector);
        if (collector.Tables.Count == 0)
            return;

        // Antes esto emitia BuildExtraReads con una lista de columnas VACIA: registraba QUE
        // TABLAS toca una subconsulta anidada, pero nunca QUE COLUMNAS. Media respuesta.
        //
        // Este es el punto unico donde convergen todos los scopes anidados: el sitio de
        // llamada generico cubre cualquier sentencia que no sea contenedor de control de
        // flujo -WHERE, lista SELECT, argumentos de funcion, RETURN de una funcion escalar-
        // y las dos llamadas especificas cubren los predicados de IF y WHILE. Resolver aqui
        // las columnas en el scope de CADA subconsulta arregla todos esos casos a la vez, en
        // vez de ir anadiendo un camino nuevo por cada constructo que aparezca.
        //
        // Medido sobre el corpus DNN: 60 de las 89 ciegas de esta tanda vivian en un scope
        // anidado. Resolver en el scope propio, con el exterior solo como respaldo para
        // referencias correlacionadas, ES la regla de ambito de T-SQL: no adivina.
        var porTabla = new List<TableColumnRef>();
        var anidadas = new NestedSubqueryCollector(followDerivedTable: true, followCte: followCte);
        fragment.Accept(anidadas);
        var sinScopeExterior = new List<(string Alias, string Table)>();
        foreach (var nqs in anidadas.Subqueries)
        {
            if (nqs.FromClause == null)
                continue;
            var propias = CollectTableRefs(nqs.FromClause, cteNames, cteBaseTables);
            // Un alias de TVF ("dbo.Fn(...) as t") CollectTableRefsInto SI lo registra
            // como "tabla" (para que la propia funcion participe en el grafo), pero no
            // conoce sus columnas de salida: filtrarlo aqui, antes de resolver por
            // scope, para que "t.col" se descarte en vez de atribuirse a la funcion
            // como si fuera una tabla real con esa columna. Caso real DNN:
            // dbo.Journal_ListForGroup, "...JOIN dbo.Journal_User_Permissions(...) as t
            // ON t.seckey = js.SecurityKey ...": "t.seckey" no tiene de donde salir.
            propias = WithoutTvfAliases(nqs.FromClause, propias);
            if (propias.Count == 0)
                continue;
            var colector = new ScopedColumnCollector();
            nqs.Accept(colector);
            FusionarLecturas(porTabla, ResolveScopedColumns(colector.Refs, propias, sinScopeExterior, tn => ResolveAllColumns(tn, ctx)));
        }

        var target = collector.Tables[0].Table;
        var iPrim = porTabla.FindIndex(t => string.Equals(t.Table, target, StringComparison.OrdinalIgnoreCase));
        var columnasPrim = iPrim >= 0 ? porTabla[iPrim].Columns : null;
        var extra = BuildExtraReads(collector.Tables, porTabla, skipFirst: true);
        AddLink(ctx, condStack, "SELECT", target, stmt, cteNames, cteBaseTables, columns: columnasPrim, extraReads: extra);
    }

    /// <summary>Collects the real source tables of every scalar/EXISTS/IN subquery
    /// (each a <see cref="ScalarSubquery"/>) reachable in a fragment, descending into
    /// nested subqueries automatically; CTE references are skipped.
    ///
    /// Tambien recoge las de una tabla derivada (QueryDerivedTable) y, si
    /// <paramref name="followCte"/> esta activo, las de un cuerpo de CTE
    /// (CommonTableExpression) - el mismo par que NestedSubqueryCollector, y por la misma
    /// razon: EmitSubqueryReads solo hace algo cuando Tables no esta vacio (su unica senal
    /// de "hay trabajo"), asi que sin esto una sentencia SIN ningun ScalarSubquery real
    /// pero CON un CTE o una tabla derivada (los dos casos reales de esta tanda,
    /// messageItems y Journal_ListForGroup - ninguno trae una ScalarSubquery consigo)
    /// volvia inmediatamente y nunca llegaba a mirarlos. followCte solo se activa para un
    /// DECLARE CURSOR (ver el porque en EmitSubqueryReads).
    ///
    /// Deduplicado POR TABLA (no por alias): una CTE recursiva referenciandose a si misma
    /// substituye el self-ref via cteBaseTables bajo un alias DISTINTO al de la propia
    /// query ("e" vs el alias original del ancla), asi que la misma tabla real podia
    /// colarse dos veces en Tables. BuildExtraReads solo deduplica sus PROPIAS entradas,
    /// no contra el target (Tables[0]), asi que una tabla repetida producia un READS_FROM
    /// duplicado. Caso real: CommunityEdgeCaseGateTests "recursive-cte" (dbo.vOrgChart),
    /// que emitia dbo.Employees dos veces en el mismo paso.
    /// </summary>
    private sealed class ScalarSubqueryTableCollector : TSqlFragmentVisitor
    {
        public readonly List<(string Alias, string Table)> Tables = new();
        private readonly HashSet<string> _cteNames;
        private readonly Dictionary<string, List<(string Alias, string Table)>> _cteBaseTables;
        private readonly bool _followCte;

        public ScalarSubqueryTableCollector(HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables, bool followCte)
        {
            _cteNames = cteNames;
            _cteBaseTables = cteBaseTables;
            _followCte = followCte;
        }

        private void AddTables(IEnumerable<FromClause> fromClauses)
        {
            foreach (var fc in fromClauses)
                foreach (var t in CollectTableRefs(fc, _cteNames, _cteBaseTables))
                    if (!Tables.Any(x => string.Equals(x.Table, t.Table, StringComparison.OrdinalIgnoreCase)))
                        Tables.Add(t);
        }

        public override void Visit(ScalarSubquery node) => AddTables(QueryFromClauses(node.QueryExpression));

        public override void Visit(QueryDerivedTable node) => AddTables(QueryFromClauses(node.QueryExpression));

        public override void Visit(CommonTableExpression node)
        {
            if (_followCte)
                AddTables(QueryFromClauses(node.QueryExpression));
        }
    }

    /// <summary>
    /// Quita de <paramref name="refs"/> (ya calculada por CollectTableRefs) los alias que
    /// en <paramref name="fromClause"/> son una funcion con valores de tabla
    /// (SchemaObjectFunctionTableReference) - CollectTableRefsInto SI la registra como
    /// "tabla" (para que la propia funcion participe en el grafo por su nombre), pero eso
    /// no significa que se conozcan sus columnas de salida: "alias.col" sobre esa entrada
    /// no debe atribuirse a la funcion como si fuera una tabla real con esa columna. Caso
    /// real DNN: dbo.Journal_ListForGroup, "...JOIN dbo.Journal_User_Permissions(...) as t
    /// ON t.seckey = js.SecurityKey ...": "t.seckey" no tiene de donde salir, "SecurityKey"
    /// (calificada por "js", una tabla real) si.
    /// </summary>
    private static List<(string Alias, string Table)> WithoutTvfAliases(FromClause? fromClause, List<(string Alias, string Table)> refs)
    {
        if (fromClause == null || refs.Count == 0)
            return refs;

        var tvfAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(TableReference tref)
        {
            switch (tref)
            {
                case SchemaObjectFunctionTableReference fn:
                    var alias = fn.Alias?.Value ?? fn.SchemaObject?.BaseIdentifier?.Value;
                    if (!string.IsNullOrEmpty(alias))
                        tvfAliases.Add(alias);
                    break;
                case QualifiedJoin qj:
                    Collect(qj.FirstTableReference);
                    Collect(qj.SecondTableReference);
                    break;
                case UnqualifiedJoin uqj:
                    Collect(uqj.FirstTableReference);
                    Collect(uqj.SecondTableReference);
                    break;
                case PivotedTableReference pvt:
                    Collect(pvt.TableReference);
                    break;
                case UnpivotedTableReference unpvt:
                    Collect(unpvt.TableReference);
                    break;
            }
        }
        foreach (var tref in fromClause.TableReferences)
            Collect(tref);

        return tvfAliases.Count == 0 ? refs : refs.Where(r => !tvfAliases.Contains(r.Alias)).ToList();
    }

    private static List<(string Alias, string Table)> CollectTableRefs(FromClause? fromClause, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var result = new List<(string Alias, string Table)>();
        if (fromClause == null)
            return result;
        foreach (var tref in fromClause.TableReferences)
            CollectTableRefsInto(tref, cteNames, cteBaseTables, result);
        return result;
    }

    /// <summary>
    /// True for "xmlCol.nodes(xquery)" / "alias.xmlCol.nodes(xquery)" - same AST shape as a
    /// schema-qualified TVF call (SchemaObjectFunctionTableReference), but it is an XML shred
    /// that BuildXmlApplyMap already resolves to its real base column; last identifier "nodes",
    /// at least "column.nodes" (2 identifiers).
    /// </summary>
    private static bool IsXmlNodesMethodCall(SchemaObjectFunctionTableReference fn) =>
        fn.SchemaObject?.Identifiers is { Count: >= 2 } ids &&
        ids[^1].Value.Equals("nodes", StringComparison.OrdinalIgnoreCase);

    private static void CollectTableRefsInto(TableReference tref, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables, List<(string Alias, string Table)> result)
    {
        switch (tref)
        {
            case NamedTableReference ntr when ntr.SchemaObject.Identifiers.Count == 1
                    && IsTriggerPseudoTable(ntr.SchemaObject.BaseIdentifier.Value, cteBaseTables, out var pseudoOnTable):
                // "inserted"/"deleted" inside a trigger body: resolve to the ON table, but
                // keep the reference's own alias (if any) so a later "i.Col"/"d.Col" still
                // resolves - unlike a plain CTE substitution, which would drop the alias.
                var pseudoEntry = (ntr.Alias?.Value ?? ntr.SchemaObject.BaseIdentifier.Value, pseudoOnTable);
                if (!result.Contains(pseudoEntry))
                    result.Add(pseudoEntry);
                break;
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
            case QueryDerivedTable qdt:
                // "(SELECT ... FROM T) alias": handled directly below via QueryFromClauses,
                // which also descends through a UNION/EXCEPT/INTERSECT (BinaryQueryExpression)
                // or parenthesized body - "(SELECT ... UNION ALL SELECT ...) alias" used to
                // match nothing here (only a bare QuerySpecification body did), silently
                // dropping every table in the derived table. Same underlying gap as the one
                // fixed in InsertSelectLineage below, just reached through FROM/USING instead
                // of an INSERT's SELECT source.
                foreach (var innerFrom in QueryFromClauses(qdt.QueryExpression))
                    foreach (var innerTref in innerFrom.TableReferences)
                        CollectTableRefsInto(innerTref, cteNames, cteBaseTables, result);
                break;
            case PivotedTableReference pvt:
                // "(subquery) PIVOT (...) AS p": the pivot wraps an inner table reference
                // (usually a derived table); its base tables are the real sources.
                CollectTableRefsInto(pvt.TableReference, cteNames, cteBaseTables, result);
                break;
            case UnpivotedTableReference unpvt:
                CollectTableRefsInto(unpvt.TableReference, cteNames, cteBaseTables, result);
                break;
            case SchemaObjectFunctionTableReference fn when fn.SchemaObject != null && !IsXmlNodesMethodCall(fn):
                // TVF as row source (FROM/JOIN/CROSS APPLY func(...)), catalog or user-defined:
                // registers alias->function name so "alias.col" resolves like any other table.
                // Excludes "xmlCol.nodes(...)" (IsXmlNodesMethodCall) - same syntax shape as a
                // schema-qualified TVF call, but it is an XML shred already resolved to its real
                // base column by BuildXmlApplyMap; registering it here too would shadow that
                // with a symbolic "ref" pseudo-column and lose the real DERIVES_FROM chain.
                result.Add((fn.Alias?.Value ?? fn.SchemaObject.BaseIdentifier?.Value ?? "", SqlText.Generate(fn.SchemaObject)));
                break;
            case OpenJsonTableReference ojtr when !IsColumnShreddedElsewhere(ojtr):
                // "FROM OPENJSON(@json[, path]) WITH (col type 'path', ...)": a virtual
                // table shredded from a scalar expression (a parameter/variable/local
                // expression - NOT a schema object, so there's no catalog entry to look up).
                // Registered as a symbolic pseudo-table named after the shredded expression
                // (e.g. "OPENJSON(@FullSensorDataArray)") purely so it plugs into the existing
                // single-table column-attribution shortcut (SplitColumnsByTable): an unqualified
                // "SELECT VehicleRegistration FROM OPENJSON(...) WITH (...)" then resolves
                // VehicleRegistration as a real read of this pseudo-table - giving INSERT...
                // SELECT genuine column lineage without parsing the WITH clause at all (the
                // WITH-declared name IS the column name the outer query selects unqualified).
                // The WITH clause's JSON path/type info itself is NOT surfaced - out of scope,
                // see the fix's writeup. Skipped (via IsColumnShreddedElsewhere) when the
                // shredded expression is itself a "alias.column" - that shape is already
                // resolved to its real base table/column by BuildXmlApplyMap below; adding a
                // pseudo-table here too would double up the read with a symbolic twin.
                result.Add((ojtr.Alias?.Value ?? "", $"OPENJSON({SqlText.Generate(ojtr.Variable)})"));
                break;
            case GlobalFunctionTableReference gftr when gftr.Name != null:
                // STRING_SPLIT(...) and other built-ins with no schema object at all
                // (GENERATE_SERIES, etc.): same reasoning as OPENJSON above - a symbolic
                // pseudo-table keyed by the call text, so the reference at least participates
                // in READS_FROM/column-attribution instead of vanishing outright.
                result.Add((gftr.Alias?.Value ?? "", $"{gftr.Name.Value}({string.Join(", ", gftr.Parameters.Select(SqlText.Generate))})"));
                break;
            case OpenQueryTableReference oqtr when oqtr.LinkedServer != null:
                // OPENQUERY(LinkedServer, 'SELECT ...'): the query text runs on a remote
                // server whose schema was never analyzed here - parsing it and minting local
                // :Table nodes for whatever names it happens to mention would risk silently
                // conflating a remote object with a same-named local one (worse than silence).
                // Register only the linked-server identity - symbolic, not a claim about a
                // real object - so the reference isn't fully invisible either.
                result.Add((oqtr.Alias?.Value ?? "", $"OPENQUERY({oqtr.LinkedServer.Value})"));
                break;
            case OpenRowsetTableReference { Object: { } orObj } orTr:
                // OPENROWSET(provider, connString, database.schema.object): the 3rd argument
                // is a genuine (remote) schema-qualified object - ScriptDom parses it into
                // .Object instead of .Query. Treated exactly like a catalog TVF: real
                // identity, flows through the normal GetOrCreateTable machinery.
                result.Add((orTr.Alias?.Value ?? orObj.BaseIdentifier?.Value ?? "", SqlText.Generate(orObj)));
                break;
            // Left unregistered, deliberately:
            //  - OpenRowsetTableReference with a literal query string (the ad hoc
            //    "provider, connString, 'SELECT ...'" form, .Query populated instead of
            //    .Object): opaque remote SQL text, same misattribution risk as an
            //    unresolved OPENQUERY above.
            //  - BulkOpenRowset (OPENROWSET(BULK 'file.csv', ...)): points at a file, not a
            //    database object - no catalog identity exists to attach a node to.
            //  - Pivots/derived tables with a non-QuerySpecification body: no alias->table
            //    mapping is possible, so columns qualified with their alias simply won't
            //    resolve below.
        }
    }

    /// <summary>
    /// True when an OPENJSON(...) call's shredded expression is itself an "alias.column"
    /// reference (e.g. "OPENJSON(t.JsonCol)") - that shape is already resolved to its real
    /// base table/column by BuildXmlApplyMap (used for both XML .nodes() and this OPENJSON
    /// form), so CollectTableRefsInto skips adding a symbolic OPENJSON(...) pseudo-table for
    /// it - only OPENJSON calls shredding a parameter/variable/other expression (no known
    /// base column to defer to) get the pseudo-table fallback.
    /// </summary>
    private static bool IsColumnShreddedElsewhere(OpenJsonTableReference ojtr) =>
        ojtr.Variable is ColumnReferenceExpression { MultiPartIdentifier.Identifiers.Count: >= 1 };

    /// <summary>
    /// Maps each CROSS/OUTER APPLY ...nodes() alias to the base XML column it shreds, so
    /// projected XML accessors ("alias.ref.value(...)") can be traced back to a real
    /// column. AdventureWorks views shred XML with
    /// <c>CROSS APPLY xmlcol.nodes('...') AS A(ref)</c> then project
    /// <c>A.ref.value('...', t)</c>; the apply target is a SchemaObjectFunctionTableReference
    /// whose name is "[qualifier.]column.nodes", invisible to alias->table resolution.
    /// Applies are walked in source order so a chained apply
    /// (<c>A.ref.nodes(...) AS B(ref)</c>) inherits A's source column.
    /// </summary>
    private static Dictionary<string, (string Table, string Column)> BuildXmlApplyMap(
        FromClause fromClause, List<(string Alias, string Table)> tableRefs)
    {
        var map = new Dictionary<string, (string Table, string Column)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tref in FlattenTableRefs(fromClause))
        {
            // OPENJSON(<jsonCol>) WITH (...) AS j: every column of alias j is shredded from
            // the single source JSON column, so map the alias to that base column exactly like
            // xml .nodes() below. Lets projected "j.Field" accessors trace back to the JSON
            // column and derive from it.
            if (tref is OpenJsonTableReference { Alias.Value: { Length: > 0 } ojAlias, Variable: ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: >= 1 } ojIds } })
            {
                var ojColumn = ojIds[^1].Value;
                var ojQualifier = ojIds.Count >= 2 ? ojIds[^2].Value : null;
                (string Table, string Column)? ojSrc =
                    ojQualifier != null && map.TryGetValue(ojQualifier, out var ojChained) ? ojChained
                    : ojQualifier != null ? ResolveAlias(ojQualifier, ojColumn, tableRefs)
                    : tableRefs.Count == 1 ? (tableRefs[0].Table, ojColumn)
                    : null;
                if (ojSrc is { } ojs)
                    map[ojAlias] = ojs;
                continue;
            }
            if (tref is not SchemaObjectFunctionTableReference fn)
                continue;
            var alias = fn.Alias?.Value;
            var ids = fn.SchemaObject?.Identifiers;
            if (alias is not { Length: > 0 } || !IsXmlNodesMethodCall(fn))
                continue;

            var column = ids![^2].Value;
            var qualifier = ids.Count >= 3 ? ids[^3].Value : null;
            (string Table, string Column)? src =
                qualifier != null && map.TryGetValue(qualifier, out var chained) ? chained
                : qualifier != null ? ResolveAlias(qualifier, column, tableRefs)
                : tableRefs.Count == 1 ? (tableRefs[0].Table, column)
                : null;
            if (src is { } s)
                map[alias] = s;
        }
        return map;
    }

    /// <summary>Resolves "alias.column" against FROM/JOIN aliases; null if the alias isn't a real table.</summary>
    private static (string Table, string Column)? ResolveAlias(string qualifier, string column, List<(string Alias, string Table)> tableRefs)
    {
        foreach (var (a, t) in tableRefs)
            if (string.Equals(a, qualifier, StringComparison.OrdinalIgnoreCase))
                return (t, column);
        return null;
    }

    /// <summary>All table references under a FROM clause in source (left-to-right) order, descending through joins/applies.</summary>
    private static IEnumerable<TableReference> FlattenTableRefs(FromClause fromClause)
    {
        IEnumerable<TableReference> Walk(TableReference t)
        {
            switch (t)
            {
                case QualifiedJoin qj:
                    foreach (var x in Walk(qj.FirstTableReference)) yield return x;
                    foreach (var x in Walk(qj.SecondTableReference)) yield return x;
                    break;
                case UnqualifiedJoin uqj:
                    foreach (var x in Walk(uqj.FirstTableReference)) yield return x;
                    foreach (var x in Walk(uqj.SecondTableReference)) yield return x;
                    break;
                default:
                    yield return t;
                    break;
            }
        }
        foreach (var tref in fromClause.TableReferences)
            foreach (var x in Walk(tref))
                yield return x;
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
    /// <summary>
    /// Índice en <paramref name="tableRefs"/> de la ÚNICA tabla cuyo esquema conocido declara
    /// esa columna, o null si no la tiene ninguna o la tienen varias. Deliberadamente no
    /// desempata: en un JOIN, dos tablas con "PortalID" son lo normal y elegir una al azar
    /// metería una arista falsa, que cuesta precisión y engaña al análisis de impacto.
    /// </summary>
    private static int? ResolveUnqualified(
        string column, List<(string Alias, string Table)> tableRefs, Func<string, List<string>?> columnsOf)
    {
        int? found = null;
        for (var i = 0; i < tableRefs.Count; i++)
        {
            if (tableRefs[i].Table.Length == 0) continue;
            var cols = columnsOf(tableRefs[i].Table);
            if (cols == null || !cols.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
            if (found != null) return null;   // ambigua
            found = i;
        }
        return found;
    }

    /// <param name="columnsOf">
    /// Resolvedor opcional tabla -&gt; lista de columnas conocidas. Sirve para colocar las
    /// columnas SIN cualificar cuando hay varias tablas en el FROM ("WHERE Archived = 1"
    /// con dos tablas unidas): sin él la columna se descarta y su lectura se pierde. Solo
    /// se asigna cuando UNA sola tabla del FROM tiene ese nombre de columna; si hay empate
    /// se sigue descartando, porque adivinar costaría precisión. Los llamadores que no
    /// tienen el catálogo a mano lo pasan nulo y mantienen el comportamiento anterior.
    /// </param>
    private static (List<string> Primary, List<TableColumnRef> Extras) SplitColumnsByTable(
        IEnumerable<(string? Qualifier, string Column)> refs, List<(string Alias, string Table)> tableRefs,
        Func<string, List<string>?>? columnsOf = null)
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
            else if (columnsOf != null && ResolveUnqualified(column, tableRefs, columnsOf) is { } hit)
            {
                table = tableRefs[hit].Table;
                isPrimary = hit == 0;
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
    /// <summary>Empty FILTERS_ON payload shared by every early-return path of <see cref="InsertSelectLineage"/> below.</summary>
    private static readonly (List<TableColumnRef> FilterColumns, List<(string Alias, string Table)> NestedTableRefs, List<string> FilterOpKinds, string FilterText, string FilterKind) EmptyFilterInfo =
        (new List<TableColumnRef>(), new List<(string Alias, string Table)>(), new List<string>(), "", "");

    private static (List<ColumnDerivation> Lineage, List<TableColumnRef> ExtraReads,
        (List<TableColumnRef> FilterColumns, List<(string Alias, string Table)> NestedTableRefs, List<string> FilterOpKinds, string FilterText, string FilterKind) FilterInfo)
        InsertSelectLineage(InsertStatement ins, IReadOnlyList<string> insColumns, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables, WalkContext ctx)
    {
        var empty = (new List<ColumnDerivation>(), new List<TableColumnRef>(), EmptyFilterInfo);

        if (ins.InsertSpecification?.InsertSource is not SelectInsertSource insSrc)
            return empty;

        // "INSERT ... SELECT ... UNION ALL SELECT ..." (also EXCEPT/INTERSECT and
        // parenthesized nesting): insSrc.Select is a BinaryQueryExpression /
        // QueryParenthesisExpression, not a single QuerySpecification, so the
        // QuerySpecification-only branch below never ran and every source read across
        // the whole statement was silently dropped - not just the second branch's.
        // Mirrors the fix for the same shape under SelectStatement (see the
        // BinaryQueryExpression/QueryParenthesisExpression case a few hundred lines up):
        // walk every branch's FROM via QueryFromClauses and union their table refs.
        // Column-level lineage isn't attempted here (a UNION's branches can each
        // project different expressions into the same insert column, and attributing
        // one expression per column across branches isn't well-defined) - only the
        // table-level READS_FROM reads are recovered, which is what was actually lost.
        if (insSrc.Select is BinaryQueryExpression or QueryParenthesisExpression)
        {
            var branchRefs = new List<(string Alias, string Table)>();
            foreach (var branchFrom in QueryFromClauses(insSrc.Select))
                foreach (var r in CollectTableRefs(branchFrom, cteNames, cteBaseTables))
                    if (!branchRefs.Contains(r))
                        branchRefs.Add(r);
            if (branchRefs.Count == 0)
                return empty;
            var unionExtraReads = BuildExtraReads(branchRefs, new List<TableColumnRef>(), skipFirst: false);
            return (new List<ColumnDerivation>(), unionExtraReads, EmptyFilterInfo);
        }

        if (insSrc.Select is not QuerySpecification qs)
            return empty;

        if (qs.FromClause == null || qs.FromClause.TableReferences.Count == 0)
            return empty;

        var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
        if (tableRefs.Count == 0)
            return empty;

        // CROSS/OUTER APPLY xml .nodes() / OPENJSON(<col>) WITH(...) aliases -> the base
        // column they shred, so "applyAlias.Field" refs resolve to (and derive from) the
        // real source column instead of dead-ending at the invisible apply alias.
        var applyMap = BuildXmlApplyMap(qs.FromClause, tableRefs);

        var allRefs = new List<(string? Qualifier, string Column)>();
        foreach (var el in qs.SelectElements)
            if (el is SelectScalarExpression sse)
            {
                var collector = new QualifiedColumnCollector();
                sse.Expression.Accept(collector);
                foreach (var rf in collector.Refs)
                    if (!(rf.Qualifier != null && applyMap.ContainsKey(rf.Qualifier)))
                        allRefs.Add(rf);
            }
        var (primaryReadCols, extrasFromCols) = SplitColumnsByTable(allRefs, tableRefs);
        // The primary FROM table (tableRefs[0]) needs its own read columns represented
        // here too, not just in the per-target-column `lineage` below: unlike a plain
        // SELECT step (whose AddLink call always attributes tableRefs[0]'s columns via
        // its own `columns:` argument), INSERT...SELECT has no other READS_FROM for its
        // primary source - see the ExtraReads doc above. Without this, "INSERT INTO
        // @tmp (...) SELECT expr(a.Col) FROM A a JOIN B b ..." (a table-variable/temp
        // target, whose DERIVES_FROM lineage below is dropped as transient) silently
        // lost every read of A's own columns - only B (a genuine "extra") survived.
        if (primaryReadCols.Count > 0)
        {
            var primaryIdx = extrasFromCols.FindIndex(e => string.Equals(e.Table, tableRefs[0].Table, StringComparison.OrdinalIgnoreCase));
            if (primaryIdx < 0)
                extrasFromCols.Insert(0, new TableColumnRef(tableRefs[0].Table, primaryReadCols));
            else
                extrasFromCols[primaryIdx] = new TableColumnRef(tableRefs[0].Table,
                    extrasFromCols[primaryIdx].Columns.Union(primaryReadCols, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        // Each apply-shredded source column is genuinely read even though its only mention
        // is the apply target (invisible to the select list) - add it as a read.
        foreach (var src in applyMap.Values)
        {
            var idx = extrasFromCols.FindIndex(e => string.Equals(e.Table, src.Table, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                extrasFromCols.Add(new TableColumnRef(src.Table, new[] { src.Column }));
            else if (!extrasFromCols[idx].Columns.Contains(src.Column, StringComparer.OrdinalIgnoreCase))
                extrasFromCols[idx] = new TableColumnRef(src.Table, extrasFromCols[idx].Columns.Append(src.Column).ToArray());
        }
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

                // Split refs into apply-shredded (resolve through the map to their base
                // column) and normal (alias->table resolution), like ViewColumnLineage.
                var normalRefs = new List<(string? Qualifier, string Column)>();
                var applyResolved = new List<(string Table, string Column)>();
                foreach (var rf in collector.Refs)
                    if (rf.Qualifier != null && applyMap.TryGetValue(rf.Qualifier, out var applySrc))
                        applyResolved.Add(applySrc);
                    else
                        normalRefs.Add(rf);

                var (primaryCols, extras) = SplitColumnsByTable(normalRefs, tableRefs);
                var exprText = SqlText.Generate(sse.Expression);
                var exprOps = OperatorClassifier.Classify(sse.Expression);
                if (primaryCols.Count > 0)
                    lineage.Add(new ColumnDerivation(insColumns[i], tableRefs[0].Table, primaryCols, exprText, exprOps));
                foreach (var extra in extras)
                    lineage.Add(new ColumnDerivation(insColumns[i], extra.Table, extra.Columns, exprText, exprOps));
                foreach (var grp in applyResolved.GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase))
                    lineage.Add(new ColumnDerivation(insColumns[i], grp.Key,
                        grp.Select(x => x.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), exprText, exprOps));
            }
        }

        // FILTERS_ON: "INSERT INTO T (...) SELECT ... FROM S [JOIN ...] WHERE ..." has its
        // WHERE clause nested inside the SELECT source, not on the InsertStatement itself -
        // AddLink's own auto-detect (ExtractFilterColumns) only recognizes SELECT/UPDATE/
        // DELETE, so this WHERE (and any JOIN ON predicates) was silently dropped for every
        // INSERT...SELECT, gated or not. Computed the same way any other step's filter
        // columns are, then returned for the caller to pass as an override.
        var filterInfo = ExtractFilterColumnsCore(qs.WhereClause, qs.FromClause.TableReferences, tableRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));

        return (lineage, extraReads, filterInfo);
    }

    /// <summary>
    /// Column lineage for "UPDATE T SET Col = expr [, ...] [FROM ...]": the value-flow
    /// counterpart of <see cref="InsertSelectLineage"/>. For each SET clause whose
    /// right-hand side references columns, emits a ColumnDerivation(Col -> source
    /// columns) with the expression text + operator tokens - so "SET Total = Price * Qty"
    /// records Total DERIVES_FROM Price/Qty (op arith:*), not just that Total was written.
    /// Sources resolve against the target table (default for unqualified columns) plus any
    /// FROM-clause tables. A column's dependency on its own prior value ("SET A = A + 1")
    /// is dropped - a self-loop adds no lineage and would just clutter the impact graph.
    /// </summary>
    private static List<ColumnDerivation> UpdateSetLineage(UpdateStatement upd, string updTarget, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        var lineage = new List<ColumnDerivation>();
        var setClauses = upd.UpdateSpecification?.SetClauses;
        if (setClauses == null || updTarget.Length == 0)
            return lineage;

        // Target table first, so unqualified columns in the SET expression default to it
        // (SplitColumnsByTable treats tableRefs[0] as the primary table).
        var tableRefs = new List<(string Alias, string Table)> { (updTarget, updTarget) };
        var fromClause = upd.UpdateSpecification?.FromClause;
        if (fromClause != null)
            foreach (var tr in CollectTableRefs(fromClause, cteNames, cteBaseTables))
                if (!tableRefs.Any(x => string.Equals(x.Alias, tr.Alias, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Table, tr.Table, StringComparison.OrdinalIgnoreCase)))
                    tableRefs.Add(tr);

        foreach (var sc in setClauses)
        {
            if (sc is not AssignmentSetClause { Column: not null, NewValue: not null } asc)
                continue;
            var targetCol = ColumnName(asc.Column);
            if (targetCol.Length == 0)
                continue;

            var collector = new QualifiedColumnCollector();
            asc.NewValue.Accept(collector);
            if (collector.Refs.Count == 0)
                continue;   // literal/parameter only - no column source

            var (primaryCols, extras) = SplitColumnsByTable(collector.Refs, tableRefs);
            var exprText = SqlText.Generate(asc.NewValue);
            var exprOps = OperatorClassifier.Classify(asc.NewValue);

            // Drop the self-reference (SET A = A + 1) from the target table's columns.
            primaryCols = primaryCols.Where(c => !string.Equals(c, targetCol, StringComparison.OrdinalIgnoreCase)).ToList();
            if (primaryCols.Count > 0)
                lineage.Add(new ColumnDerivation(targetCol, tableRefs[0].Table, primaryCols, exprText, exprOps));
            foreach (var extra in extras)
                lineage.Add(new ColumnDerivation(targetCol, extra.Table, extra.Columns, exprText, exprOps));
        }
        return lineage;
    }

    /// <summary>
    /// Column lineage for a MERGE's action clauses - the previously-missing piece that
    /// left MERGE with table-level READS_FROM/WRITES_TO but zero column lineage. Handles
    /// WHEN MATCHED THEN UPDATE SET (like <see cref="UpdateSetLineage"/>) and WHEN NOT
    /// MATCHED THEN INSERT (cols) VALUES (exprs) (like the VALUES path of an INSERT): each
    /// target column DERIVES_FROM the source column(s) in its expression. Columns resolve
    /// against the target (alias-qualified or unqualified) plus the USING source table(s);
    /// the target's own prior value is dropped as a self-loop.
    /// </summary>
    private static List<ColumnDerivation> MergeLineage(MergeStatement mrg, string mrgTarget, List<(string Alias, string Table)> tableRefs)
    {
        var lineage = new List<ColumnDerivation>();
        var spec = mrg.MergeSpecification;
        if (spec == null || mrgTarget.Length == 0)
            return lineage;

        void AddFrom(string targetCol, ScalarExpression expr)
        {
            if (targetCol.Length == 0) return;
            var collector = new QualifiedColumnCollector();
            expr.Accept(collector);
            if (collector.Refs.Count == 0) return;   // literal/parameter only
            var (primaryCols, extras) = SplitColumnsByTable(collector.Refs, tableRefs);
            var exprText = SqlText.Generate(expr);
            var exprOps = OperatorClassifier.Classify(expr);
            primaryCols = primaryCols.Where(c => !string.Equals(c, targetCol, StringComparison.OrdinalIgnoreCase)).ToList();
            if (primaryCols.Count > 0)
                lineage.Add(new ColumnDerivation(targetCol, tableRefs[0].Table, primaryCols, exprText, exprOps));
            foreach (var extra in extras)
                lineage.Add(new ColumnDerivation(targetCol, extra.Table, extra.Columns, exprText, exprOps));
        }

        foreach (var clause in spec.ActionClauses)
        {
            switch (clause.Action)
            {
                case UpdateMergeAction uma:
                    foreach (var sc in uma.SetClauses)
                        if (sc is AssignmentSetClause { Column: not null, NewValue: not null } asc)
                            AddFrom(ColumnName(asc.Column), asc.NewValue);
                    break;

                case InsertMergeAction { Source: ValuesInsertSource { RowValues.Count: > 0 } vis } ima:
                    var insCols = ima.Columns.Select(ColumnName).ToList();
                    var row = vis.RowValues[0].ColumnValues;
                    if (insCols.Count > 0 && insCols.Count == row.Count)
                        for (int i = 0; i < insCols.Count; i++)
                            AddFrom(insCols[i], row[i]);
                    break;
            }
        }
        return lineage;
    }

    /// <summary>
    /// Every column a WHEN MATCHED UPDATE SET actually assigns, independent of whether
    /// MergeLineage could trace that assignment's right-hand side back to a real source
    /// table. Mirrors UpdateColumns() for a plain UPDATE, which lists every SET target
    /// unconditionally: WRITES_COLUMN should reflect "this column got written", not "this
    /// column's lineage happened to be resolvable". See the call site's comment (the
    /// MergeStatement case in Walk()) for the real corpus example this fixes.
    ///
    /// Deliberately UPDATE-only, NOT WHEN NOT MATCHED INSERT's column list too (tried and
    /// measured): sys.dm_sql_referenced_entities - the oracle eval/column-recall/blind-refs.md
    /// is measured against - reliably reports a MERGE's UPDATE SET targets but NOT its INSERT
    /// column list (unlike a plain INSERT, where it does). Adding INSERT columns here made 7
    /// DNN procedures (UpdateHostSetting, UpdateModuleSetting, UpdatePortalSetting,
    /// UpdateRoleSetting, UpdateTabModuleSetting, UpdateTabSetting, AuthCookies_Update) emit
    /// WRITES_COLUMN for CreatedByUserID/CreatedOnDate/UserID/CreatedOn that the oracle never
    /// references, dropping the "direct" precision floor from 99.7846% to 99.5% (measured).
    /// The columns are likely real writes - this is an oracle blind spot, not a false claim -
    /// but the invariant is "never guess past what's measured", so this stays scoped to the
    /// branch the oracle actually corroborates. A resolvable INSERT source (a real table, e.g.
    /// "INSERT (Id, Price) VALUES (s.Id, s.Price)") is still covered via mrgLineage above,
    /// unaffected by this scoping.
    /// </summary>
    private static List<string> MergeTargetColumns(MergeStatement mrg)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clause in mrg.MergeSpecification?.ActionClauses ?? Enumerable.Empty<MergeActionClause>())
        {
            if (clause.Action is not UpdateMergeAction uma)
                continue;
            foreach (var sc in uma.SetClauses)
                if (sc is AssignmentSetClause { Column: not null } asc)
                {
                    var name = ColumnName(asc.Column);
                    if (name.Length > 0 && seen.Add(name))
                        result.Add(name);
                }
        }
        return result;
    }

    /// <summary>
    /// Column lineage for a view body ("CREATE VIEW v AS SELECT expr AS Out, ... FROM ..."):
    /// each output column DERIVES_FROM the base table column(s) in its SELECT expression,
    /// with the expression text + operator tokens - so a view is a first-class lineage hop
    /// (Out -> base.Col), not just a black box that "reads" tables. Output names come from
    /// the explicit view column list when given, else the SELECT element's alias, else a
    /// bare column reference's own name (an unnamed computed column is skipped - it has no
    /// stable name to attribute). Only a single QuerySpecification is handled; UNION/EXCEPT
    /// view bodies are left for a later pass rather than guessed positionally.
    /// </summary>
    public static List<ColumnDerivation> ViewColumnLineage(SelectStatement select, IReadOnlyList<string>? explicitColumns)
    {
        var lineage = new List<ColumnDerivation>();

        // A view body is a single QuerySpecification OR a set operation
        // (UNION/EXCEPT/INTERSECT). Process every branch: output column names are
        // positional, taken from the first branch (SQL semantics) or the explicit view
        // column list, and each branch contributes its own source columns at that position.
        var branches = QuerySpecs(select.QueryExpression).Where(b => b.FromClause != null).ToList();
        if (branches.Count == 0)
            return lineage;

        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cteBaseTables = new Dictionary<string, List<(string Alias, string Table)>>(StringComparer.OrdinalIgnoreCase);
        CollectCteNames(select, cteNames, cteBaseTables);

        var first = branches[0];
        var hasExplicit = explicitColumns is { Count: > 0 } && explicitColumns.Count == first.SelectElements.Count;
        string OutName(int i, QuerySpecification branch) =>
            hasExplicit ? explicitColumns![i]
            : i < first.SelectElements.Count && first.SelectElements[i] is SelectScalarExpression fsse ? ViewOutputName(fsse)
            : branch.SelectElements[i] is SelectScalarExpression bsse ? ViewOutputName(bsse)
            : "";

        foreach (var branch in branches)
        {
            var tableRefs = CollectTableRefs(branch.FromClause!, cteNames, cteBaseTables);
            if (tableRefs.Count == 0)
                continue;

            // CROSS/OUTER APPLY xmlcol.nodes() aliases -> the base XML column they shred,
            // so projected "applyAlias.ref.value(...)" accessors resolve to a real column.
            var xmlApply = BuildXmlApplyMap(branch.FromClause!, tableRefs);

            for (int i = 0; i < branch.SelectElements.Count; i++)
            {
                if (branch.SelectElements[i] is not SelectScalarExpression sse)
                    continue;   // SelectStarExpression etc. - can't name the columns without schema

                var outName = OutName(i, branch);
                if (outName.Length == 0)
                    continue;

                var collector = new QualifiedColumnCollector();
                sse.Expression.Accept(collector);

                // Split off references whose qualifier is a CROSS APPLY alias: resolve
                // those through the apply map to their base XML column; the rest go
                // through the normal alias->table resolution.
                var normalRefs = new List<(string? Qualifier, string Column)>();
                var xmlResolved = new List<(string Table, string Column)>();
                foreach (var rf in collector.Refs)
                {
                    if (rf.Qualifier != null && xmlApply.TryGetValue(rf.Qualifier, out var xmlSrc))
                        xmlResolved.Add(xmlSrc);
                    else
                        normalRefs.Add(rf);
                }

                var (primaryCols, extras) = SplitColumnsByTable(normalRefs, tableRefs);
                var exprText = SqlText.Generate(sse.Expression);
                var exprOps = OperatorClassifier.Classify(sse.Expression);
                var beforeCount = lineage.Count;
                if (primaryCols.Count > 0)
                    lineage.Add(new ColumnDerivation(outName, tableRefs[0].Table, primaryCols, exprText, exprOps));
                foreach (var extra in extras)
                    lineage.Add(new ColumnDerivation(outName, extra.Table, extra.Columns, exprText, exprOps));
                foreach (var grp in xmlResolved.GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase))
                    lineage.Add(new ColumnDerivation(outName, grp.Key,
                        grp.Select(x => x.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), exprText, exprOps));

                // El nombre de salida es cierto aunque no se pueda trazar su origen: una
                // función niládica ("dbo.SuperUserTabID() AS SuperTabId", sin ninguna
                // columna referenciada) o una columna calificada por un alias de
                // subconsulta derivada ("S.MaxSortOrder" donde S es "(SELECT ... ) S",
                // que SplitColumnsByTable descarta por no ser una tabla real) dejan esta
                // vuelta sin ninguna ColumnDerivation. Sin registrar el nombre, un
                // consumidor con "SELECT * FROM estaVista" nunca sabe que esa columna
                // existe. Se registra sin fuente (SourceTable="") - no hay DERIVES_FROM
                // que dibujar, pero el nombre no se inventa: viene literal del alias/nombre
                // de columna de la propia vista.
                if (lineage.Count == beforeCount)
                    lineage.Add(new ColumnDerivation(outName, "", Array.Empty<string>(), exprText, exprOps));
            }
        }
        return lineage;
    }

    /// <summary>Every QuerySpecification (SELECT block) inside a query expression, descending through UNION/EXCEPT/INTERSECT and parentheses, in source order.</summary>
    private static IEnumerable<QuerySpecification> QuerySpecs(QueryExpression? qe)
    {
        switch (qe)
        {
            case QuerySpecification qs:
                yield return qs;
                break;
            case BinaryQueryExpression bqe:
                foreach (var b in QuerySpecs(bqe.FirstQueryExpression)) yield return b;
                foreach (var b in QuerySpecs(bqe.SecondQueryExpression)) yield return b;
                break;
            case QueryParenthesisExpression qpe:
                foreach (var b in QuerySpecs(qpe.QueryExpression)) yield return b;
                break;
        }
    }

    /// <summary>Output column name of a view SELECT element: alias if given, else a bare column reference's own name, else "" (unnamed computed column).</summary>
    private static string ViewOutputName(SelectScalarExpression sse)
    {
        if (sse.ColumnName?.Value is { Length: > 0 } alias)
            return alias;
        if (sse.Expression is ColumnReferenceExpression cre)
            return ColumnName(cre);
        return "";
    }

    /// <summary>
    /// "SET @var = (SELECT ... FROM T [JOIN ...] WHERE ...)": previously invisible -
    /// CollectAssignment only records which column(s) of T feed @var (ASSIGNED_FROM,
    /// still emitted separately, unaffected by this), with no Step/READS_FROM/
    /// FILTERS_ON, so T and its WHERE/JOIN predicates never showed up in the graph or
    /// flowchart. Now also emitted as a "SELECT" step (detail names the variable it
    /// feeds), mirroring the top-level "SELECT @var = Col FROM T WHERE ..." case in
    /// Walk's SelectStatement branch. No-ops for a plain literal/expression (no
    /// ScalarSubquery) or a subquery with no FROM clause.
    /// </summary>
    private static void WalkScalarSubqueryAssignment(string varName, ScalarExpression expr, TSqlStatement stmt, List<Condition> condStack, WalkContext ctx, HashSet<string> cteNames, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        while (expr is ParenthesisExpression pe)
            expr = pe.Expression;
        if (expr is not ScalarSubquery { QueryExpression: QuerySpecification { FromClause.TableReferences.Count: > 0 } qs })
            return;

        var tableRefs = CollectTableRefs(qs.FromClause, cteNames, cteBaseTables);
        if (tableRefs.Count == 0)
            return;
        var target = tableRefs[0].Table;

        // Columns actually selected - mirrors Walk's SelectStatement branch, minus the
        // SELECT * case (a scalar subquery has exactly one select element by construction).
        var refs = new List<(string? Qualifier, string Column)>();
        foreach (var el in qs.SelectElements)
            if (el is SelectScalarExpression sse)
            {
                var collector = new QualifiedColumnCollector();
                sse.Expression.Accept(collector);
                refs.AddRange(collector.Refs);
            }
        var (selColumns, extras) = SplitColumnsByTable(refs, tableRefs, tn => ResolveAllColumns(tn, ctx));
        var extraReads = BuildExtraReads(tableRefs, extras, skipFirst: true);

        var (filterColumns, nestedTableRefs, filterOpKinds, filterText, filterKind) = ExtractFilterColumnsCore(qs.WhereClause, qs.FromClause.TableReferences, tableRefs, cteNames, cteBaseTables, tn => ResolveAllColumns(tn, ctx));
        if (nestedTableRefs.Count > 0)
            foreach (var (_, nestedTable) in nestedTableRefs)
                if (!extraReads.Any(e => string.Equals(e.Table, nestedTable, StringComparison.OrdinalIgnoreCase)))
                    extraReads.Add(new TableColumnRef(nestedTable, Array.Empty<string>()));

        AddLink(ctx, condStack, "SELECT", target, stmt, cteNames, cteBaseTables,
            columns: selColumns, extraReads: extraReads, filterColumnsOverride: filterColumns,
            filterOpKindsOverride: filterOpKinds, filterTextOverride: filterText,
            filterKindOverride: filterKind, detail: $"→ {varName}");
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
        // Operator tokens of this RHS, unioned into the variable's running set - covers
        // both value math ("@a + @b") and dynamic-SQL string building ("'...' + @name").
        var ops = OperatorClassifier.Classify(expr);
        if (ops.Count > 0)
        {
            if (!ctx.VariableOpKinds.TryGetValue(varName, out var opSet))
                ctx.VariableOpKinds[varName] = opSet = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var op in ops)
                opSet.Add(op);
        }

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
    /// produces, supporting string literals, parentheses, literal-valued @variables,
    /// "a + b" concatenation of those, QUOTENAME(...), NCHAR(n)/CHAR(n) of a literal code,
    /// COALESCE(...), and CASE WHEN/THEN/ELSE (see ResolveBoolean for the WHEN condition
    /// evaluation). Returns null for anything
    /// runtime-dependent (column refs, params with no literal value, other function calls,
    /// CASE branches whose condition can't be determined, etc.).
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
            // QUOTENAME(@x) / QUOTENAME(@x, quoteChar): a very common wrapper around an
            // otherwise-literal identifier piece (schema/table name) in dynamic-DDL builder
            // procs - without this case, a single QUOTENAME() call anywhere in a "+"
            // concatenation made the WHOLE dynamic_sql resolve to null (ConcatLiterals bails
            // on the first unresolved part), even when every variable involved was a plain
            // SET @var = N'literal' a few lines earlier. Real case: WideWorldImporters'
            // DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad builds 34 EXEC(@SQL)
            // trigger DROP/CREATE statements from QUOTENAME(@SchemaName)+'.'+QUOTENAME(@TableName)
            // where @SchemaName/@TableName are always literal SETs - all 34 were silently
            // unresolved (dynamic_sql=="") before this fix.
            case FunctionCall fc when string.Equals(fc.FunctionName?.Value, "QUOTENAME", StringComparison.OrdinalIgnoreCase)
                && fc.Parameters is { Count: 1 or 2 }:
            {
                var inner = ResolveLiteral(fc.Parameters[0], ctx);
                if (inner == null)
                {
                    // PROTOTYPE (dynsql-placeholder): QUOTENAME(@x) couldn't resolve @x to a
                    // literal (e.g. @x is an input parameter, never a SET literal - the
                    // sp_BlitzIndex shape: "... + QUOTENAME(@DatabaseName) + '.[sys].[objects]'").
                    // QUOTENAME's presence is a syntactic guarantee that this position is an
                    // IDENTIFIER, not a clause - so instead of bailing the whole dynamic SQL to
                    // unresolved, substitute a placeholder identifier token for @x. This loses
                    // the concrete identifier (we never learn which database @x names at
                    // static-analysis time) but keeps the surrounding SQL parseable, so the
                    // real table/columns/operation still get extracted. Downstream
                    // (GraphExporter) detects this token in the reconstructed target name and
                    // marks the resulting edge as inferred (confidence < 1.0, bound_to the
                    // param, database_unknown) - never indistinguishable from a certain edge.
                    // Restricted to a BARE variable reference: QUOTENAME(@a + @b) or
                    // QUOTENAME(some_func(@a)) has no single param to bind the inference to,
                    // so those still fail closed (return null) as before.
                    if (fc.Parameters[0] is VariableReference placeholderVar)
                    {
                        ctx.DynamicSqlPlaceholderParams.Add(placeholderVar.Name);
                        inner = PlaceholderPrefix + placeholderVar.Name + PlaceholderSuffix;
                    }
                    else
                    {
                        return null;
                    }
                }
                var quote = fc.Parameters.Count == 2 ? ResolveLiteral(fc.Parameters[1], ctx) : "[";
                return quote switch
                {
                    "[" or "]" => $"[{inner.Replace("]", "]]")}]",
                    "'" => $"'{inner.Replace("'", "''")}'",
                    "\"" => $"\"{inner.Replace("\"", "\"\"")}\"",
                    "`" => $"`{inner.Replace("`", "``")}`",
                    _ => null,
                };
            }
            // NCHAR(n) / CHAR(n): produce a single character from a code point. The common
            // real use is building separators - WideWorldImporters'
            // DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad sets
            // @CrLf = NCHAR(13) + NCHAR(10) and concatenates it into every CREATE TRIGGER
            // body it builds. Without this case @CrLf never resolved, so all 17 CREATE
            // TRIGGER EXEC steps stayed unresolved (the 17 DROP TRIGGER steps don't use
            // @CrLf, which is why only half resolved after the QUOTENAME fix). Restricted to
            // a literal integer code in the BMP, non-surrogate, so the result is a single
            // well-defined char; anything else fails closed.
            case FunctionCall fc when (string.Equals(fc.FunctionName?.Value, "NCHAR", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fc.FunctionName?.Value, "CHAR", StringComparison.OrdinalIgnoreCase))
                && fc.Parameters is { Count: 1 }
                && fc.Parameters[0] is IntegerLiteral codeLit
                && int.TryParse(codeLit.Value, out var code)
                && code >= 0 && code <= 0xFFFF && !char.IsSurrogate((char)code):
                return ((char)code).ToString();
            // COALESCE(a, b, ...): ScriptDom models this as its own AST node (not a
            // FunctionCall). Real T-SQL semantics: first argument that is non-NULL at
            // runtime wins. Since ctx.ResolvedVars only contains variables actually SET to a
            // literal, "ResolveLiteral resolves" and "is non-NULL here" coincide for the
            // patterns this walker tracks, so "first part that resolves" is a faithful
            // static evaluation - not just a best-effort guess.
            case CoalesceExpression coalesce:
            {
                foreach (var part in coalesce.Expressions)
                {
                    var coalesced = ResolveLiteral(part, ctx);
                    if (coalesced != null)
                        return coalesced;
                }
                return null;
            }
            // CASE WHEN ... THEN ... [WHEN ... THEN ...] [ELSE ...] END: walk the WHEN
            // clauses in order (matching real CASE evaluation), statically evaluating each
            // condition via ResolveBoolean. The first clause whose condition can't be
            // determined aborts the whole CASE to null (fails closed) - we can't know
            // whether an earlier untaken branch would have matched.
            case SearchedCaseExpression sce:
            {
                foreach (var when in sce.WhenClauses)
                {
                    var cond = ResolveBoolean(when.WhenExpression, ctx);
                    if (cond == null)
                        return null;
                    if (cond == true)
                        return ResolveLiteral(when.ThenExpression, ctx);
                }
                return sce.ElseExpression != null ? ResolveLiteral(sce.ElseExpression, ctx) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Best-effort static evaluation of a boolean expression to true/false, supporting
    /// "=" / "&lt;&gt;" / "!=" comparisons of two ResolveLiteral-resolvable operands, IS
    /// [NOT] NULL, parentheses and AND/OR of those. Returns null (unknown) for anything else
    /// - in particular for comparison types other than equals/not-equals (ordinal string
    /// comparison would not reliably match SQL Server's collation rules for ordering), which
    /// keeps callers like the SearchedCaseExpression case above failing closed rather than
    /// risking a wrong branch.
    /// </summary>
    private static bool? ResolveBoolean(BooleanExpression? expr, WalkContext ctx)
    {
        switch (expr)
        {
            case BooleanParenthesisExpression p:
                return ResolveBoolean(p.Expression, ctx);
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and1:
            {
                var l = ResolveBoolean(and1.FirstExpression, ctx);
                var r = ResolveBoolean(and1.SecondExpression, ctx);
                return l == null || r == null ? null : l & r;
            }
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or1:
            {
                var l = ResolveBoolean(or1.FirstExpression, ctx);
                var r = ResolveBoolean(or1.SecondExpression, ctx);
                return l == null || r == null ? null : l | r;
            }
            case BooleanComparisonExpression cmp:
            {
                var l = ResolveLiteral(cmp.FirstExpression, ctx);
                var r = ResolveLiteral(cmp.SecondExpression, ctx);
                if (l == null || r == null)
                    return null;
                return cmp.ComparisonType switch
                {
                    BooleanComparisonType.Equals => string.Equals(l, r, StringComparison.Ordinal),
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                        => !string.Equals(l, r, StringComparison.Ordinal),
                    _ => (bool?)null,
                };
            }
            case BooleanIsNullExpression isNull:
            {
                // A literal value resolved here means the expression is definitely
                // non-NULL at this point; an unresolved value tells us nothing (it may be
                // genuinely NULL at runtime, or just outside what this walker tracks).
                var resolved = ResolveLiteral(isNull.Expression, ctx);
                return resolved == null ? null : isNull.IsNot;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// For a dynamic EXEC ("EXEC(@sql)" / "EXEC sp_executesql @sql, ..."), reconstructs the
    /// executed SQL when it resolves to a pure literal, returned whitespace-collapsed and
    /// USE-stripped. "" when built at runtime (the common case). Returned in FULL (untruncated)
    /// because the same string feeds SqlAnalyzer.ResolveDynamicSqlLinks, which re-parses it for
    /// the inner DML's own lineage - truncating here would silently break re-parse of any
    /// dynamic SQL longer than the display cap. The display cap (200) is applied downstream, at
    /// the point the value becomes the descriptive "dynamic_sql" node property (GraphExporter).
    /// </summary>
    private static string ResolveExecLiteral(ExecutableEntity? entity, WalkContext ctx)
    {
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
        return collapsed;
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

        // XML data-type method calls ("xmlcol.value(...)", "alias.ref.query(...)",
        // "col.exist(...)"): the accessed XML column is the call target, which is a
        // MultiPartIdentifierCallTarget (NOT a ColumnReferenceExpression), so it is
        // otherwise invisible to column collection. Record it as a column reference of
        // its target so the XML column is tracked as a read and as a lineage source.
        // The final identifier of the target is the column ("ref" for apply-exposed
        // rowsets, or the XML column itself); the preceding identifier is its qualifier
        // (a FROM/JOIN alias, or a CROSS APPLY alias resolved separately via the apply map).
        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is not MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } ids })
                return;
            if (!IsXmlAccessorMethod(node.FunctionName?.Value))
                return;
            var column = ids[^1].Value;
            var qualifier = ids.Count > 1 ? ids[^2].Value : null;
            Refs.Add((qualifier, column));
        }
    }

    /// <summary>XML data-type accessor methods usable in a projection (value/query/exist).</summary>
    private static bool IsXmlAccessorMethod(string? name) =>
        name is not null && (
            name.Equals("value", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("query", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>True if a (possibly schema-qualified) name is a single-part identifier matching a registered CTE alias.</summary>
    private static bool IsCte(SchemaObjectName name, HashSet<string> cteNames) =>
        name.Identifiers.Count == 1 && cteNames.Contains(name.BaseIdentifier.Value);

    /// <summary>
    /// True if <paramref name="name"/> is the trigger pseudo-table "inserted"/"deleted"
    /// seeded into <paramref name="cteBaseTables"/> for the trigger body being walked,
    /// yielding the real ON table it resolves to. Only ever true inside a trigger -
    /// nothing seeds these names for any other object, so non-trigger walks are unchanged.
    /// </summary>
    private static bool IsTriggerPseudoTable(string name, Dictionary<string, List<(string Alias, string Table)>> cteBaseTables, out string onTable)
    {
        onTable = "";
        if ((name.Equals("inserted", StringComparison.OrdinalIgnoreCase) || name.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            && cteBaseTables.TryGetValue(name, out var bases) && bases.Count == 1)
        {
            onTable = bases[0].Table;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Looks up the full column list of <paramref name="tableName"/> (a "schema.table"
    /// name as produced by TargetName/TableRefName) in ctx.TableColumns, scoped to
    /// ctx.Db. Returns null if the table is unknown (no CREATE TABLE was analyzed for
    /// it) or tableName is empty (CTE/table variable/derived table).
    /// </summary>
    private static List<string>? ResolveAllColumns(string tableName, WalkContext ctx)
    {
        if (tableName.Length == 0)
            return null;

        var plain = SqlText.NormalizeRef(tableName);
        if (ctx.TryGetColumns($"{ctx.Db}::{plain}", out var cols))
            return cols;

        // "SELECT * FROM TabModules" (sin esquema) frente a un catálogo que registra
        // "dbo.TabModules": la clave sin cualificar no casa y la expansión devolvía la
        // lista vacía, perdiendo la lista de selección entera. Medido sobre el corpus
        // DNN eran 122 columnas en 29 módulos, la mayor bolsa de columnas que el motor
        // no veía y para la que no había explicación.
        //
        // El respaldo solo se intenta cuando la búsqueda exacta ya ha fallado, así que
        // nunca pisa una resolución buena; en el peor caso acierta el esquema por
        // defecto, que es lo que SQL Server haría con ese mismo SQL.
        return !plain.Contains('.') && ctx.TryGetColumns($"{ctx.Db}::dbo.{plain}", out var dboCols)
            ? dboCols
            : null;
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
    private static string TargetName(TableReference? target, HashSet<string> cteNames, FromClause? fromClause = null,
        Dictionary<string, List<(string Alias, string Table)>>? cteBaseTables = null)
    {
        // INSERT/UPDATE into a table variable: @TableVar
        if (target is VariableTableReference vtr)
            return vtr.Variable?.Name ?? "";

        if (target is not NamedTableReference ntr)
            return "";

        if (IsCte(ntr.SchemaObject, cteNames))
            return "";

        if (ntr.SchemaObject.Identifiers.Count == 1 && fromClause != null)
        {
            var resolved = ResolveAlias(ntr.SchemaObject.BaseIdentifier.Value, fromClause.TableReferences, cteNames,
                cteBaseTables ?? new Dictionary<string, List<(string Alias, string Table)>>(StringComparer.OrdinalIgnoreCase));
            if (resolved != null)
                return resolved;
        }

        return SqlText.Generate(ntr.SchemaObject);
    }

    /// <summary>
    /// Searches a FROM clause's table references (recursing into JOINs) for a
    /// table aliased as <paramref name="alias"/>, and returns its real name so the
    /// bare alias of an "UPDATE alias ... FROM real AS alias" / "DELETE alias FROM
    /// real AS alias" is never mistaken for a table. Resolves:
    ///   - a NamedTableReference to its base/#temp/##global name ("" if it's a CTE);
    ///   - a VariableTableReference (table variable "@t AS alias") to its @name, so
    ///     it flows through the same temp/variable guard as a direct write to it and
    ///     never surfaces as a :Table node.
    /// Recurses through every JoinTableReference (INNER/LEFT/CROSS and comma joins).
    /// Also resolves a QueryDerivedTable ("(SELECT ... FROM RealTable) AS alias") whose
    /// alias matches: an UPDATE/DELETE target is only updatable through a derived table
    /// when that derived table ultimately reads exactly one real base table (Ola
    /// Hallengren's "UPDATE QueueDatabase SET ... FROM (SELECT TOP 1 ... FROM
    /// dbo.QueueDatabase ...) QueueDatabase" is exactly this shape) - so when the derived
    /// table flattens (via CollectTableRefsInto, same logic a FROM clause partner read
    /// already uses) to a single distinct table, that table is the real target; ambiguous
    /// (0 or 2+ tables, e.g. a join inside the derived table) falls through to null so the
    /// caller keeps its current fallback instead of guessing.
    /// Null when no alias matches, so the caller keeps its current behavior.
    /// </summary>
    private static string? ResolveAlias(string alias, IList<TableReference> refs, HashSet<string> cteNames,
        Dictionary<string, List<(string Alias, string Table)>> cteBaseTables)
    {
        foreach (var tref in refs)
        {
            switch (tref)
            {
                case NamedTableReference ntr when string.Equals(ntr.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase):
                    return IsCte(ntr.SchemaObject, cteNames) ? "" : SqlText.Generate(ntr.SchemaObject);
                case VariableTableReference vtr when string.Equals(vtr.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase):
                    return vtr.Variable?.Name ?? "";
                case QueryDerivedTable qdt when string.Equals(qdt.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase):
                    var innerRefs = new List<(string Alias, string Table)>();
                    CollectTableRefsInto(qdt, cteNames, cteBaseTables, innerRefs);
                    var distinctTables = innerRefs.Select(r => r.Table)
                        .Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return distinctTables.Count == 1 ? distinctTables[0] : null;
                case JoinTableReference jtr:
                    var found = ResolveAlias(alias, new[] { jtr.FirstTableReference, jtr.SecondTableReference }, cteNames, cteBaseTables);
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

    /// <summary>
    /// Classifies the executed entity of an EXEC ("EXECUTE ...") or an INSERT's
    /// EXEC-as-source ("INSERT INTO T EXEC ..."). Takes the ExecutableEntity directly
    /// (rather than the enclosing ExecuteStatement) so both call sites - a real
    /// ExecuteStatement and an InsertStatement's ExecuteInsertSource - share the exact
    /// same classification logic instead of duplicating/drifting it.
    /// </summary>
    private static (string target, bool isDynamic, List<string> dynamicVars) ExecTarget(ExecutableEntity? entity)
    {
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

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            // A table-valued function invoked as a table source ("FROM dbo.tvf(...)",
            // "CROSS/OUTER APPLY dbo.tvf(...)"): collected so a CALLS edge links the caller
            // to the TVF exactly like a scalar UDF call - otherwise the TVF (and its
            // transitive reads of base tables) is invisible to impact analysis. XML
            // ".nodes()" shredding and other pseudo-functions produce names that match no
            // analyzed object, so GraphExporter filters them out (resolution against the
            // known object set), the same way built-in scalar functions are ignored.
            if (node.SchemaObject == null)
                return;
            var name = SqlText.Generate(node.SchemaObject);
            if (_seen.Add(name))
                Names.Add(name);
        }
    }
}
