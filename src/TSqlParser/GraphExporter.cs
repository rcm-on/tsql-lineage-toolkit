using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TSqlParser;

// GraphNode / GraphRel / GraphPayload viven ahora en Parser.Contracts.GraphModel,
// compartidos con NetParser: los dos extractores emiten el mismo grafo. Aqui se
// resuelven por el "global using Parser.Contracts" de GlobalUsings.cs.

/// <summary>
/// Converts a list of per-object ObjectResult (from SqlAnalyzer) into a uniform
/// nodes/relationships graph, matching the shape of src/neo4j_exporter.py's
/// rule-engine subgraph:
///
///   (:SqlObject)-[:DECLARES]->(:Variable)
///   (:SqlObject)-[:HAS_PARAMETER]->(:Parameter)
///   (:SqlObject)-[:HAS_STEP {order}]->(:Step)-[:ACTION]->(:Action)
///   (:Rule {type, expression})-[:GOVERNS]->(:Step)
///   (:Step)-[:TARGETS]->(:SqlObject)        when the target is a known proc/function/view
///   (:Step)-[:WRITES_TO]->(:Table)          for INSERT/UPDATE/DELETE/MERGE/ALTER targets
///   (:Step)-[:READS_FROM]->(:Table)         for SELECT ... FROM targets
///   (:Step)-[:FILTERS_ON]->(:Column)        WHERE/JOIN-ON columns (--columns only) - what
///                                            decided which rows the step touched, separate
///                                            from READS_COLUMN/WRITES_COLUMN ("what got
///                                            read/written"); also populated for steps
///                                            injected from a resolved dynamic-SQL literal
///   (:SqlObject)-[:CALLS]->(:SqlObject)     caller -> callee, de-duplicated
///
/// Node ids are "<Database>::<Schema.Object>" for SqlObjects (globally unique
/// and stable across runs), "<Database>:table:<name>" for plain data tables,
/// and "<ObjectId>#step<N>" / "<Database>:action:<name>" / "<Database>:rule:<hash>"
/// for the rest. :SqlObject and :Table are deliberately distinct labels so a
/// query (or downstream coupling) can immediately tell "this Step touches a
/// PROCEDURE" from "this Step touches a TABLE" without inspecting the name.
/// </summary>
public static class GraphExporter
{
    /// <param name="includeColumns">
    /// When true, also emits :Column nodes per table (deduplicated, "{tableId}:column:{name}")
    /// linked (:Table)-[:HAS_COLUMN]->(:Column), and links each Step to the columns it
    /// touches: (:Step)-[:WRITES_COLUMN]->(:Column) for INSERT/UPDATE,
    /// (:Step)-[:READS_COLUMN]->(:Column) for SELECT. Off by default since column lists
    /// are only as complete as the SQL text (e.g. "INSERT INTO T SELECT *" yields none).
    /// </param>
    /// <param name="tableSchemas">
    /// Optional CREATE TABLE definitions (from TableAnalyzer). When provided (and
    /// includeColumns is true), each table's full column list is emitted up front
    /// with real metadata - data_type, is_nullable, is_identity, is_primary_key,
    /// ordinal - via (:Table)-[:HAS_COLUMN {ordinal}]->(:Column). Foreign keys become
    /// (:Column)-[:REFERENCES {constraint}]->(:Column) plus a derived
    /// (:Table)-[:FK_TO {constraint}]->(:Table) for ER-diagram-style queries
    /// (many child rows -> one parent row). Steps from the procedural pass below
    /// then attach to these same typed Column nodes instead of creating untyped ones.
    /// </param>
    public static GraphPayload Build(List<ObjectResult> results, bool includeColumns = false, List<TableSchemaResult>? tableSchemas = null)
    {
        var graph = new GraphPayload();

        // SqlObject node per analyzed object, plus a lookup so EXEC targets /
        // CALLS can be resolved back to a node id within the same database.
        var objectIds = new HashSet<string>();
        var byPlainName = new Dictionary<(string db, string plain), string>();

        // viewBaseTables: for every analyzed VIEW, the real base table(s)/column(s)
        // its own SELECT body ultimately reads - built from that VIEW's own SELECT
        // step(s) (Columns = primary-table columns, ExtraReads = JOIN partners),
        // the same data every other object's own SELECT step already carries. Lets
        // a consumer's "SELECT ... FROM AnalyzedView" bridge straight through to the
        // view's base table(s) instead of dead-ending at the VIEW's SqlObject node.
        var viewBaseTables = new Dictionary<string, List<(string Table, string Column)>>();

        // Every analyzed VIEW's ObjectName, so a reader's "SELECT ... FROM AnalyzedView"
        // step can also land a READS_COLUMN on the VIEW's own :Column nodes (the ones
        // BuildViewLineage already created under the view's table-scheme id), not just on
        // the via_view expansion to base tables below. Without this, a query against a
        // view that renames a column in its SELECT list (e.g. "M.PortalID AS
        // [OwnerPortalID]") never gets a READS_COLUMN matching that renamed name anywhere:
        // via_view only carries the base column's ORIGINAL name (PortalID), so the two
        // disagree and the renamed output column is silently unrepresented on the view
        // entity that a consumer (and the DMV oracle) actually expects it on.
        var viewObjectIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in results)
        {
            var (db, plain) = SplitName(r.ObjectName);
            objectIds.Add(r.ObjectName);
            // Keyed by NormalizeRef(db) (not the raw db string) so a cross-database
            // CALLS/EXEC lookup - whose database segment comes from arbitrary SQL
            // text casing, not from an ObjectName - reliably matches regardless of
            // case (see ResolveCalleeKey below).
            // Synonyms are deliberately excluded: a reference to a synonym must resolve
            // as a *table read* of its base object (redirected later by ResolveSynonyms),
            // not as an object reference (TARGETS) that would shadow the READS_FROM.
            if (r.ObjectType != "SYNONYM")
                byPlainName[(NormalizeRef(db), NormalizeRef(plain))] = r.ObjectName;

            if (r.ObjectType == "VIEW")
            {
                viewObjectIds.Add(r.ObjectName);
                var bases = new List<(string Table, string Column)>();
                foreach (var flv in r.FlowLinks)
                {
                    if (flv.ConsequenceType != "SELECT" || IsTempOrVariable(flv.ConsequenceTarget))
                        continue;
                    foreach (var col in flv.Columns)
                        bases.Add((flv.ConsequenceTarget, col));
                    foreach (var extra in flv.ExtraReads)
                        if (!IsTempOrVariable(extra.Table))
                            foreach (var col in extra.Columns)
                                bases.Add((extra.Table, col));
                }
                if (bases.Count > 0)
                    viewBaseTables[r.ObjectName] = bases;
            }

            var (schemaName, shortName) = SplitSchemaObject(plain);
            graph.Nodes.Add(new GraphNode
            {
                Id = r.ObjectName,
                Labels = new List<string> { "SqlObject", "Process" },
                Properties = new Dictionary<string, object>
                {
                    ["database"] = db,
                    ["schema"] = schemaName,
                    ["name"] = shortName,
                    ["full_name"] = plain,
                    ["object_type"] = r.ObjectType,
                    ["has_transaction"] = r.HasTransaction,
                    ["has_error_handling"] = r.HasErrorHandling,
                    ["has_cursor"] = r.HasCursor,
                    ["dynamic_sql_calls"] = r.DynamicSqlCount,
                    ["cyclomatic_complexity"] = r.ComplexityScore,
                    ["parse_error"] = r.Error ?? "",
                },
            });
        }

        var actionIds = new Dictionary<(string db, string name), string>();
        var ruleIds = new Dictionary<(string db, string type, string text), string>();
        var tableIds = new Dictionary<(string db, string name), string>();
        var columnIds = new Dictionary<(string tableId, string column), string>();
        var nestedRelSeen = new HashSet<(string child, string parent)>();
        // WHERE-derived :BusinessRule nodes (see the FilterText block below) - separate
        // id namespace/cache from ruleIds (which holds the IF/WHILE :Rule/GOVERNS chain)
        // so the two never collide even if text happens to match.
        var whereRuleIds = new Dictionary<(string db, string text), string>();
        var whereConstrainsSeen = new HashSet<(string ruleId, string colId)>();

        // Maps (db, unqualified short name) -> the one qualified name ("dbo.Foo")
        // registered under it, so an unqualified reference ("Foo") that later shows up
        // in a step (e.g. "SELECT * FROM Foo", or an UPDATE target the AST couldn't
        // fully qualify) resolves to the SAME :Table node as "dbo.Foo" instead of
        // minting a twin - see GetOrCreateTable. A null value means the short name is
        // ambiguous (two distinct qualified tables share it, e.g. "dbo.Foo" and
        // "archive.Foo"), which disables the shortcut rather than guessing wrong.
        var tableShortNames = new Dictionary<(string db, string shortName), string?>();

        // ── Table schemas (CREATE TABLE): always emit Table nodes + FK_TO edges
        // (useful for ER-diagram lineage without --columns). Column nodes and
        // HAS_COLUMN / REFERENCES are added only when includeColumns is true.
        if (tableSchemas is { Count: > 0 })
            BuildTableSchemas(graph, tableSchemas, tableIds, tableShortNames, columnIds, includeColumns);

        if (includeColumns)
            BuildViewLineage(graph, results, tableIds, tableShortNames, columnIds);

        foreach (var r in results)
        {
            var (db, _) = SplitName(r.ObjectName);

            // tempOrigin: for every step in THIS object that writes into a
            // #temp/@table-variable target (e.g. "INSERT #Staging SELECT Col FROM
            // RealTable"), maps (NormalizeRef(tempTarget), TargetColumn) -> the real
            // origin(s) AstWalker already computed in that step's own ColumnLineage.
            // That ColumnLineage is otherwise silently dropped below (temp/variable
            // targets never get their own WRITES_TO/DERIVES_FROM branch - see
            // "!IsTempOrVariable(fl.ConsequenceTarget)" further down), so without
            // this map a later "INSERT RealTable2 SELECT Col FROM #Staging" would
            // dead-end its DERIVES_FROM at a phantom #Staging Table node instead of
            // bridging straight through to RealTable. Scoped per-object: a temp
            // table only lives for the duration of one procedure's batch.
            var tempOrigin = new Dictionary<(string Table, string Column), List<(string SourceTable, string SourceColumn, string Logic, int LineNo, string StepId)>>();
            for (int ord = 0; ord < r.FlowLinks.Count; ord++)
            {
                var flx = r.FlowLinks[ord];
                if (!IsTempOrVariable(flx.ConsequenceTarget))
                    continue;
                var stepIdX = $"{r.ObjectName}#step{ord}";
                var tempKey = NormalizeRef(flx.ConsequenceTarget);
                foreach (var deriv in flx.ColumnLineage)
                {
                    var key = (tempKey, deriv.TargetColumn);
                    if (!tempOrigin.TryGetValue(key, out var list))
                        tempOrigin[key] = list = new();
                    foreach (var srcCol in deriv.SourceColumns)
                        list.Add((deriv.SourceTable, srcCol, deriv.TransformationExpression, flx.LineNo, stepIdX));
                }
            }

            // Follows tempOrigin through any number of #temp/@var hops (capped to
            // avoid runaway recursion on a self-referencing chain) down to the real
            // table(s) a transient column ultimately came from. Empty when the chain
            // can't be resolved (e.g. the temp was filled by "SELECT *" or something
            // other than a positional INSERT...SELECT) - no edge is invented in that
            // case, same as today's behavior for an unresolvable derivation.
            List<(string Table, string Column, string Logic, int LineNo, string StepId, List<string> Via)> ResolveTransient(
                string table, string column, List<string> viaChain, int depth)
            {
                if (!IsTempOrVariable(table) || depth > 5)
                    return new();
                if (!tempOrigin.TryGetValue((NormalizeRef(table), column), out var origins))
                    return new();

                var resolved = new List<(string, string, string, int, string, List<string>)>();
                var nextVia = new List<string>(viaChain) { table };
                foreach (var (srcTable, srcCol, logic, lineNo, stepIdX) in origins)
                {
                    if (IsTempOrVariable(srcTable))
                        resolved.AddRange(ResolveTransient(srcTable, srcCol, nextVia, depth + 1));
                    else
                        resolved.Add((srcTable, srcCol, logic, lineNo, stepIdX, nextVia));
                }
                return resolved;
            }

            // ── Parameters ───────────────────────────────────────────────
            foreach (var p in r.Parameters)
            {
                var paramId = $"{r.ObjectName}#param:{p.Name}";
                graph.Nodes.Add(new GraphNode
                {
                    Id = paramId,
                    Labels = new List<string> { "Parameter" },
                    Properties = new Dictionary<string, object>
                    {
                        ["name"] = p.Name,
                        ["data_type"] = p.DataType,
                        ["is_output"] = p.IsOutput,
                    },
                });
                graph.Relationships.Add(new GraphRel
                {
                    Type = "HAS_PARAMETER",
                    StartNodeId = r.ObjectName,
                    EndNodeId = paramId,
                });
            }

            // ── Variables ────────────────────────────────────────────────
            foreach (var v in r.Variables)
            {
                var varId = $"{r.ObjectName}#var:{v.Name}";
                graph.Nodes.Add(new GraphNode
                {
                    Id = varId,
                    Labels = new List<string> { "Variable" },
                    Properties = new Dictionary<string, object>
                    {
                        ["name"] = v.Name,
                        ["data_type"] = v.Type,
                        ["default"] = v.Default,
                        // Assignment RHS texts ("'CREATE INDEX ' + @Name", ...) in
                        // source order - reconstructs how a dynamic-SQL string is built.
                        ["construction"] = r.VariableConstructions.TryGetValue(v.Name, out var ctor) ? ctor : new List<string>(),
                        // Structured operators used across all assignments to this variable
                        // (see OperatorClassifier) - e.g. ["concat:+"] flags a string built
                        // by concatenation (dynamic-SQL injection surface), ["arith:*"] a
                        // numeric computation. Empty for a plain literal/column assignment.
                        ["op_kinds"] = r.VariableOpKinds.TryGetValue(v.Name, out var vops) ? vops : new List<string>(),
                    },
                });
                graph.Relationships.Add(new GraphRel
                {
                    Type = "DECLARES",
                    StartNodeId = r.ObjectName,
                    EndNodeId = varId,
                });
            }

            // ── ASSIGNED_FROM: (:Variable)-[:ASSIGNED_FROM]->(:Column) for
            // "SET @var = (SELECT Col FROM S ...)" / "SELECT @var = Col FROM S ...",
            // i.e. column-to-variable lineage - column-level, so only when --columns.
            if (includeColumns)
            {
                foreach (var va in r.VariableAssignments)
                {
                    string? varId = null;
                    if (r.Variables.Any(v => string.Equals(v.Name, va.VariableName, StringComparison.OrdinalIgnoreCase)))
                        varId = $"{r.ObjectName}#var:{va.VariableName}";
                    else if (r.Parameters.Any(p => string.Equals(p.Name, va.VariableName, StringComparison.OrdinalIgnoreCase)))
                        varId = $"{r.ObjectName}#param:{va.VariableName}";

                    if (varId == null)
                        continue;

                    // "SELECT @var = Col FROM #temp/@tableVar": the source is transient and
                    // must NOT be materialized as a :Table node (same policy the WRITES_TO /
                    // DERIVES_FROM / FILTERS_ON branches already enforce). Without this guard
                    // every "SELECT @x = c FROM #staging" minted a phantom #staging :Table -
                    // the 11 FRK ghost temp tables all came in through here. Bridging a
                    // variable back through a temp is a separate (task-b) concern; here we
                    // just keep the Table graph free of transient nodes.
                    if (IsTempOrVariable(va.SourceTable))
                        continue;

                    var (srcTableId, srcTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, va.SourceTable);
                    foreach (var srcCol in va.SourceColumns)
                    {
                        var srcColId = GetOrCreateColumn(graph, columnIds, srcTableId, srcTableName, srcCol);
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "ASSIGNED_FROM",
                            StartNodeId = varId,
                            EndNodeId = srcColId,
                        });
                    }
                }
            }

            // ── Steps / Actions / Rules ──────────────────────────────────
            for (int order = 0; order < r.FlowLinks.Count; order++)
            {
                var fl = r.FlowLinks[order];
                var stepId = $"{r.ObjectName}#step{order}";

                graph.Nodes.Add(new GraphNode
                {
                    Id = stepId,
                    Labels = new List<string> { "Step" },
                    Properties = new Dictionary<string, object>
                    {
                        ["order"] = order,
                        ["sequence"] = order + 1,
                        ["line_no"] = fl.LineNo,
                        ["nesting_level"] = fl.NestingLevel,
                        ["action"] = fl.ConsequenceType,
                        ["step_type"] = ClassifyStepType(fl.ConsequenceType, fl.ConsequenceTarget),
                        ["target_name"] = fl.ConsequenceTarget,
                        ["detail"] = fl.Detail,
                        // Display cap: DynamicSqlText carries the FULL resolved literal (so
                        // ResolveDynamicSqlLinks can re-parse it); truncate only here, where it
                        // becomes the descriptive node property, to keep nodestore files small.
                        ["dynamic_sql"] = SqlText.Truncate(fl.DynamicSqlText, 200),
                        ["is_dynamic_sql"] = fl.DynamicSqlVars.Count > 0,
                        ["select_star"] = fl.SelectStar,
                        ["condition_path"] = fl.ConditionPath,
                        ["condition_keys"] = fl.ConditionKeys,
                        ["label"] = $"{fl.ConsequenceType}{(fl.Detail.Length > 0 ? $" ({fl.Detail})" : "")} -> {fl.ConsequenceTarget}".TrimEnd(' ', '-', '>'),
                    },
                });
                graph.Relationships.Add(new GraphRel
                {
                    Type = "HAS_STEP",
                    StartNodeId = r.ObjectName,
                    EndNodeId = stepId,
                    Properties = { ["order"] = order },
                });

                // For "EXEC (dynamic SQL)" steps: link to the @variables that were
                // concatenated into the executed string, so a reader can trace
                // "this step runs whatever SQL @SQL happens to hold, and @SQL was
                // built from these inputs" without opening the source.
                foreach (var varName in fl.DynamicSqlVars)
                {
                    var varId = $"{r.ObjectName}#var:{varName}";
                    if (r.Variables.Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)))
                    {
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "BUILDS_SQL_FROM",
                            StartNodeId = stepId,
                            EndNodeId = varId,
                        });
                    }
                }

                // USES_VARIABLE: every @variable referenced anywhere in this step's
                // statement (WHERE/SET/VALUES/predicates, ...) that the object also
                // DECLARES or receives as a parameter - lets a reader see "this step's
                // behavior depends on the current value of @X" regardless of whether
                // @X feeds dynamic SQL.
                foreach (var varName in fl.UsedVariables)
                {
                    string? usedId = null;
                    if (r.Variables.Any(v => string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase)))
                        usedId = $"{r.ObjectName}#var:{varName}";
                    else if (r.Parameters.Any(p => string.Equals(p.Name, varName, StringComparison.OrdinalIgnoreCase)))
                        usedId = $"{r.ObjectName}#param:{varName}";

                    if (usedId != null)
                    {
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "USES_VARIABLE",
                            StartNodeId = stepId,
                            EndNodeId = usedId,
                        });
                    }
                }

                // Action node, de-duplicated per database
                var actionKey = (db, fl.ConsequenceType);
                if (!actionIds.TryGetValue(actionKey, out var actionId))
                {
                    actionId = $"{db}:action:{fl.ConsequenceType}";
                    actionIds[actionKey] = actionId;
                    graph.Nodes.Add(new GraphNode
                    {
                        Id = actionId,
                        Labels = new List<string> { "Action" },
                        Properties = new Dictionary<string, object> { ["name"] = fl.ConsequenceType },
                    });
                }
                graph.Relationships.Add(new GraphRel
                {
                    Type = "ACTION",
                    StartNodeId = stepId,
                    EndNodeId = actionId,
                });

                // ExtraReads: "... FROM A JOIN B ..." also reads from B (and further
                // JOIN partners) - each gets its own READS_FROM, plus READS_COLUMN for
                // the columns referenced via "b.Col". Extracted as a local so it can
                // fire both when the consequence target is a real table AND when it is a
                // #temp/@table-variable (e.g. "INSERT #Staging SELECT FROM RealTable" -
                // the temp write isn't graphed, but the read of RealTable is real).
                void EmitExtraReads()
                {
                    foreach (var extra in fl.ExtraReads)
                    {
                        if (IsTempOrVariable(extra.Table))
                            continue;

                        var (extraTableId, extraTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, extra.Table);
                        var (isExtraCrossDb, extraTargetDb) = DetectCrossDb(extra.Table, db);
                        var extraRelProps = new Dictionary<string, object>
                        {
                            ["action_type"] = fl.ConsequenceType,
                            ["table"] = extra.Table,
                            ["via"] = "JOIN",
                        };
                        if (isExtraCrossDb)
                        {
                            extraRelProps["is_cross_database"] = true;
                            extraRelProps["source_database"] = db;
                            extraRelProps["target_database"] = extraTargetDb;
                        }
                        MarkIfDynamicPlaceholder(extraRelProps, extra.Table);
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "READS_FROM",
                            StartNodeId = stepId,
                            EndNodeId = extraTableId,
                            Properties = extraRelProps,
                        });

                        if (includeColumns)
                        {
                            foreach (var colName in extra.Columns)
                            {
                                var colId = GetOrCreateColumn(graph, columnIds, extraTableId, extraTableName, colName);
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "READS_COLUMN",
                                    StartNodeId = stepId,
                                    EndNodeId = colId,
                                    Properties = { ["resolution"] = fl.SelectStar ? "star_expanded" : "direct" },
                                });
                            }
                        }
                    }
                }

                // Target resolution: either another analyzed SqlObject (proc/function/
                // view/trigger - e.g. EXEC of a sibling proc, or INSERT...SELECT off a
                // TVF) or, far more commonly, a plain data Table that was never itself
                // analyzed. Both get their own node so a reader (or query) can tell
                // "this Step touches PROCEDURE X" apart from "this Step touches TABLE Y"
                // without guessing from the name.
                if (fl.ConsequenceTarget.Length > 0 &&
                    byPlainName.TryGetValue((NormalizeRef(db), NormalizeRef(fl.ConsequenceTarget)), out var targetObjId))
                {
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "TARGETS",
                        StartNodeId = stepId,
                        EndNodeId = targetObjId,
                    });

                    // Direct read of the VIEW's own output columns (as opposed to the
                    // via_view expansion to base tables below): "this step reads
                    // AnalyzedView.OutName" for whatever OutName it actually wrote/expanded
                    // (fl.Columns - literal column list, or the star-expansion catalog for
                    // "SELECT *"). Lands on the SAME :Column node BuildViewLineage already
                    // created for the view (same table-scheme id, GetOrCreateTable below
                    // uses the view's own (db, plain) exactly like BuildViewLineage does),
                    // so a view that renames a source column in its SELECT list is still
                    // matched by its OWN name - not just by the base table's original name,
                    // which is all via_view can offer.
                    if (fl.ConsequenceType == "SELECT" && viewObjectIds.Contains(targetObjId) && includeColumns)
                    {
                        var (viewDb, viewPlain) = SplitName(targetObjId);
                        var (viewTableId, viewTableName) = GetOrCreateTable(graph, tableIds, tableShortNames, viewDb, viewPlain);
                        foreach (var colName in fl.Columns)
                        {
                            var viewColId = GetOrCreateColumn(graph, columnIds, viewTableId, viewTableName, colName);
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = "READS_COLUMN",
                                StartNodeId = stepId,
                                EndNodeId = viewColId,
                                Properties = { ["resolution"] = fl.SelectStar ? "star_expanded" : "direct" },
                            });
                        }
                    }

                    // VIEW expansion: "SELECT ... FROM AnalyzedView" also reads straight
                    // through to the view's own real base table(s)/column(s) - not just
                    // the VIEW's SqlObject node - so impact analysis on a base table finds
                    // every consumer of a view built on it. Read-side only: a write into a
                    // VIEW (rare, requires an updatable view) still resolves only as far as
                    // TARGETS today.
                    if (fl.ConsequenceType == "SELECT" && viewBaseTables.TryGetValue(targetObjId, out var viewBases))
                    {
                        var viewDb = SplitName(targetObjId).db;
                        var seenViewTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (baseTable, baseCol) in viewBases)
                        {
                            var (baseTableId, baseTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,viewDb, baseTable);
                            if (seenViewTables.Add(baseTable))
                            {
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "READS_FROM",
                                    StartNodeId = stepId,
                                    EndNodeId = baseTableId,
                                    Properties = new Dictionary<string, object>
                                    {
                                        ["action_type"] = fl.ConsequenceType,
                                        ["table"] = baseTable,
                                        ["via_view"] = targetObjId,
                                    },
                                });
                            }
                            if (includeColumns)
                            {
                                var baseColId = GetOrCreateColumn(graph, columnIds, baseTableId, baseTableName, baseCol);
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "READS_COLUMN",
                                    StartNodeId = stepId,
                                    EndNodeId = baseColId,
                                    // Misma marca que la arista READS_FROM de arriba: esta lectura no está
                                    // escrita en el SQL, se alcanza atravesando la vista hasta su tabla base.
                                    // Sin distinguirla, un consumidor no puede saber si "lee Portals.Name"
                                    // es literal o transitivo — y al medir contra las DMV (que se paran en
                                    // la vista) toda esta clase parece un fallo cuando no lo es.
                                    Properties = new Dictionary<string, object>
                                    {
                                        ["via_view"] = targetObjId,
                                        ["resolution"] = "via_view",
                                    },
                                });
                            }
                        }
                    }
                }
                else if (fl.ConsequenceTarget.Length > 0 && fl.ConsequenceType is "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "SELECT" or "ALTER" or "TRUNCATE" or "OUTPUT" && !IsTempOrVariable(fl.ConsequenceTarget))
                {
                    var (tableId, tableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, fl.ConsequenceTarget);
                    var relType = fl.ConsequenceType is "SELECT" ? "READS_FROM" : "WRITES_TO";
                    var (isCrossDb, targetDbName) = DetectCrossDb(fl.ConsequenceTarget, db);
                    var relProps = new Dictionary<string, object>
                    {
                        ["action_type"] = fl.ConsequenceType,
                        ["table"] = fl.ConsequenceTarget,
                    };
                    if (isCrossDb)
                    {
                        relProps["is_cross_database"] = true;
                        relProps["source_database"] = db;
                        relProps["target_database"] = targetDbName;
                    }
                    MarkIfDynamicPlaceholder(relProps, fl.ConsequenceTarget);
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = relType,
                        StartNodeId = stepId,
                        EndNodeId = tableId,
                        Properties = relProps,
                    });

                    // Optional column-level detail: which columns of the table this
                    // step reads (SELECT list) or writes (INSERT column list /
                    // UPDATE SET clauses). Lets a Rule -[:GOVERNS]-> Step ->
                    // [:WRITES_COLUMN]-> Column query show exactly which business
                    // rules affect a given column.
                    if (includeColumns)
                    {
                        var colRelType = fl.ConsequenceType == "SELECT" ? "READS_COLUMN" : "WRITES_COLUMN";
                        foreach (var colName in fl.Columns)
                        {
                            var colId = GetOrCreateColumn(graph, columnIds, tableId, tableName, colName);
                            var colRelProps = new Dictionary<string, object>
                            {
                                // Clase de evidencia de esta arista: "direct" si la columna está
                                // escrita literalmente en el SQL, "star_expanded" si vino de expandir
                                // un SELECT * / alias.* contra el esquema. Mismo criterio que
                                // BuildGraphRefsByClass en ColumnRecallGateTests, pero emitido aquí
                                // en el punto donde el motor ya conoce la respuesta, no derivado
                                // a posteriori.
                                ["resolution"] = fl.SelectStar ? "star_expanded" : "direct",
                            };
                            // ALTER's Detail ("DROP COLUMN"/"ALTER COLUMN") disambiguates a
                            // schema-changing WRITES_COLUMN from an ordinary INSERT/UPDATE
                            // write - an impact query for "what breaks if I drop this column"
                            // filters on this instead of treating every writer as a consumer.
                            if (fl.ConsequenceType == "ALTER" && fl.Detail.Length > 0)
                                colRelProps["detail"] = fl.Detail;
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = colRelType,
                                StartNodeId = stepId,
                                EndNodeId = colId,
                                Properties = colRelProps,
                            });
                        }

                        // CONDITIONED_BY: for "UPDATE T SET Col = ... WHERE FilterCol = ...",
                        // (:Column T.Col)-[:CONDITIONED_BY]->(:Column FilterCol) per written
                        // column - business-rule lineage ("what determined this row got
                        // mutated") rather than data-flow lineage (DERIVES_FROM, below).
                        // UPDATE is the only case where Columns (SET targets) and
                        // FilterColumns (WHERE/JOIN-ON) coexist with a direct causal link.
                        if (fl.ConsequenceType == "UPDATE" && fl.FilterColumns.Count > 0)
                        {
                            foreach (var colName in fl.Columns)
                            {
                                var writtenColId = GetOrCreateColumn(graph, columnIds, tableId, tableName, colName);
                                foreach (var filterCol in fl.FilterColumns)
                                {
                                    if (IsTempOrVariable(filterCol.Table))
                                        continue;

                                    var (filterTableId, filterTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, filterCol.Table);
                                    foreach (var filterColName in filterCol.Columns)
                                    {
                                        var filterColId = GetOrCreateColumn(graph, columnIds, filterTableId, filterTableName, filterColName);
                                        var condProps = new Dictionary<string, object>
                                        {
                                            ["line_no"] = fl.LineNo,
                                            ["caused_by_step"] = stepId,
                                        };
                                        AddOpKinds(condProps, fl.FilterOpKinds);
                                        graph.Relationships.Add(new GraphRel
                                        {
                                            Type = "CONDITIONED_BY",
                                            StartNodeId = writtenColId,
                                            EndNodeId = filterColId,
                                            Properties = condProps,
                                        });
                                    }
                                }
                            }
                        }

                        // DERIVES_FROM: for "INSERT INTO T (...) SELECT ... FROM S ...",
                        // (:Column T.Col)-[:DERIVES_FROM]->(:Column S.SrcCol) per target
                        // column whose value was positionally traced back to source
                        // column(s) of S - column-to-column lineage for ETL-style procs.
                        foreach (var deriv in fl.ColumnLineage)
                        {
                            var targetColId = GetOrCreateColumn(graph, columnIds, tableId, tableName, deriv.TargetColumn);

                            // SourceTable is a #temp/@table-variable bridge (not the real
                            // origin): never create a Table node for it (it doesn't exist
                            // once the batch ends) - instead chase tempOrigin through to
                            // the real table(s), tagging the edge with via_transient so the
                            // bridge is still visible without polluting the Table graph.
                            if (IsTempOrVariable(deriv.SourceTable))
                            {
                                foreach (var srcCol in deriv.SourceColumns)
                                {
                                    foreach (var origin in ResolveTransient(deriv.SourceTable, srcCol, new List<string>(), 0))
                                    {
                                        var (origTableId, origTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, origin.Table);
                                        var origColId = GetOrCreateColumn(graph, columnIds, origTableId, origTableName, origin.Column);
                                        var transientProps = new Dictionary<string, object>
                                        {
                                            ["logic"] = deriv.TransformationExpression,
                                            ["line_no"] = fl.LineNo,
                                            ["caused_by_step"] = stepId,
                                            ["via_transient"] = string.Join(" -> ", origin.Via),
                                            ["origin_logic"] = origin.Logic,
                                            ["origin_line_no"] = origin.LineNo,
                                            ["origin_step"] = origin.StepId,
                                        };
                                        AddOpKinds(transientProps, deriv.OpKinds);
                                        graph.Relationships.Add(new GraphRel
                                        {
                                            Type = "DERIVES_FROM",
                                            StartNodeId = targetColId,
                                            EndNodeId = origColId,
                                            Properties = transientProps,
                                        });
                                    }
                                }
                                continue;
                            }

                            var (srcTableId, srcTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, deriv.SourceTable);
                            foreach (var srcCol in deriv.SourceColumns)
                            {
                                var srcColId = GetOrCreateColumn(graph, columnIds, srcTableId, srcTableName, srcCol);
                                var derivProps = new Dictionary<string, object>
                                {
                                    ["logic"] = deriv.TransformationExpression,
                                    ["line_no"] = fl.LineNo,
                                    ["caused_by_step"] = stepId,
                                };
                                AddOpKinds(derivProps, deriv.OpKinds);
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "DERIVES_FROM",
                                    StartNodeId = targetColId,
                                    EndNodeId = srcColId,
                                    Properties = derivProps,
                                });
                            }
                        }
                    }

                    EmitExtraReads();
                }
                else if (IsTempOrVariable(fl.ConsequenceTarget)
                    && fl.ConsequenceType is "INSERT" or "SELECT" or "UPDATE" or "DELETE" or "MERGE")
                {
                    // Target is a #temp/@table-variable (never graphed as a Table), but
                    // the statement still reads real source tables - those reads are real
                    // and must surface even though the write target is transient.
                    EmitExtraReads();
                }

                // FILTERS_ON: columns from this step's own WHERE/JOIN-ON predicates -
                // "what decided which rows got touched", as opposed to READS_COLUMN/
                // WRITES_COLUMN ("what got read/written"). Independent of which branch
                // above resolved the consequence target (or whether it resolved to
                // another SqlObject), and works the same for steps injected by
                // SqlAnalyzer.ResolveDynamicSqlLinks - dynamic SQL's own WHERE clause
                // surfaces here too, since FilterColumns is just carried over from the
                // inner re-parsed FlowLinkInfo.
                if (includeColumns)
                {
                    foreach (var filterCol in fl.FilterColumns)
                    {
                        if (IsTempOrVariable(filterCol.Table))
                            continue;

                        var (filterTableId, filterTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, filterCol.Table);
                        foreach (var colName in filterCol.Columns)
                        {
                            var colId = GetOrCreateColumn(graph, columnIds, filterTableId, filterTableName, colName);
                            var filtersProps = new Dictionary<string, object>
                            {
                                ["resolution"] = fl.SelectStar ? "star_expanded" : "direct",
                            };
                            AddOpKinds(filtersProps, fl.FilterOpKinds);
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = "FILTERS_ON",
                                StartNodeId = stepId,
                                EndNodeId = colId,
                                Properties = filtersProps,
                            });
                        }
                    }
                }

                // WHERE-derived :BusinessRule: this step's own WHERE clause (not the
                // JOIN ON predicates also folded into FilterColumns - FilterText is
                // WHERE-only, see its doc comment on FlowLinkInfo) captured as a
                // first-class rule, the same node shape as the DDL CHECK/DEFAULT/UNIQUE
                // :BusinessRule nodes emitted in EmitTableSchemas (HAS_RULE from the
                // owner, CONSTRAINS from the rule to each governed column) - so
                // AuditExporter's business_rules count picks both sources up uniformly
                // with no changes needed there. Deliberately NOT the :Rule/GOVERNS pair
                // used for IF/WHILE below: a WHERE qualifies one step, it doesn't branch
                // the flowchart the way GOVERNS does, so reusing that edge would show a
                // false decision point. Gated on FilterText (not FilterColumns) being
                // non-empty so a step with only a JOIN and no WHERE never manufactures a
                // rule out of the join's key-matching predicate.
                if (includeColumns && fl.FilterText.Length > 0 && fl.FilterColumns.Count > 0)
                {
                    var whereRuleKey = (db, fl.FilterText);
                    if (!whereRuleIds.TryGetValue(whereRuleKey, out var whereRuleId))
                    {
                        var affectedTables = fl.FilterColumns
                            .Where(fc => !IsTempOrVariable(fc.Table))
                            .Select(fc => GetOrCreateTable(graph, tableIds, tableShortNames, db, fc.Table).tableName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        whereRuleId = $"{db}:bizrule:where:{StableHash(fl.FilterText)}";
                        whereRuleIds[whereRuleKey] = whereRuleId;
                        graph.Nodes.Add(new GraphNode
                        {
                            Id = whereRuleId,
                            Labels = new List<string> { "BusinessRule" },
                            Properties = new Dictionary<string, object>
                            {
                                ["kind"] = "WHERE",
                                ["expression"] = fl.FilterText,
                                // domain_filter | key_lookup | mixed (FilterRuleClassifier) -
                                // never used to drop the rule, just to let a consumer skip
                                // key_lookup rules if it wants only genuine domain logic.
                                ["filter_kind"] = fl.FilterKind,
                                ["tables"] = affectedTables,
                            },
                        });
                    }

                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "HAS_RULE",
                        StartNodeId = stepId,
                        EndNodeId = whereRuleId,
                    });

                    foreach (var filterCol in fl.FilterColumns)
                    {
                        if (IsTempOrVariable(filterCol.Table))
                            continue;

                        var (whereTableId, whereTableName) = GetOrCreateTable(graph, tableIds, tableShortNames, db, filterCol.Table);
                        foreach (var colName in filterCol.Columns)
                        {
                            var colId = GetOrCreateColumn(graph, columnIds, whereTableId, whereTableName, colName);
                            if (whereConstrainsSeen.Add((whereRuleId, colId)))
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "CONSTRAINS",
                                    StartNodeId = whereRuleId,
                                    EndNodeId = colId,
                                });
                        }
                    }
                }

                // Rule chain: one Rule node per entry in condition_path (de-duplicated
                // per database), linked outermost -> innermost via NESTED_IN so the
                // whole decision tree is walkable in Neo4j without parsing arrays.
                // Only the innermost (= fl.ConditionType/ConditionText) gets GOVERNS
                // to this step; siblings under the same ancestor share the same
                // upper Rule nodes.
                if (fl.ConditionType != "UNCONDITIONAL")
                {
                    string? parentRuleId = null;
                    string innermostRuleId = "";
                    foreach (var entry in fl.ConditionPath)
                    {
                        var sep = entry.IndexOf(": ", StringComparison.Ordinal);
                        var condType = entry[..sep];
                        var condText = entry[(sep + 2)..];

                        var ruleKey = (db, condType, condText);
                        if (!ruleIds.TryGetValue(ruleKey, out var ruleId))
                        {
                            ruleId = $"{db}:rule:{condType}:{StableHash(condText)}";
                            ruleIds[ruleKey] = ruleId;
                            graph.Nodes.Add(new GraphNode
                            {
                                Id = ruleId,
                                Labels = new List<string> { "Rule", condType.Replace("_", "") },
                                Properties = new Dictionary<string, object>
                                {
                                    ["type"] = condType,
                                    ["expression"] = condText,
                                },
                            });
                        }

                        if (parentRuleId != null)
                        {
                            var nestedKey = (ruleId, parentRuleId);
                            if (nestedRelSeen.Add(nestedKey))
                            {
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "NESTED_IN",
                                    StartNodeId = ruleId,
                                    EndNodeId = parentRuleId,
                                });
                            }
                        }

                        parentRuleId = ruleId;
                        innermostRuleId = ruleId;
                    }

                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "GOVERNS",
                        StartNodeId = innermostRuleId,
                        EndNodeId = stepId,
                    });
                }
            }

            // ── CALLS: caller -> callee. callee may be a bare "Schema.Object" (same
            // database as the caller) or a 3-part "OtherDb.Schema.Object" (cross-
            // database EXEC/function call) - resolved against byPlainName using the
            // TARGET database, not always the caller's, so cross-db calls to an
            // object that *was* analyzed actually resolve instead of silently
            // falling through to no edge at all (the previous behavior).
            foreach (var callee in r.ExecCalls)
            {
                var (calleeDb, calleePlain, isCrossDbCall) = ResolveCalleeKey(callee, db);
                // caller == callee (direct recursion) is a legitimate CALLS edge, not
                // noise: an object recursing into itself still "calls" itself and must
                // show up in via_calls/workflows, or it reads as a leaf that calls
                // nobody. The mutual-recursion cycle (A->B->A) already proves every
                // downstream traversal (AFFECTS BFS, change_map, nodestore workflows)
                // is cycle-safe, so there is no reason to special-case the 1-node cycle.
                if (byPlainName.TryGetValue((calleeDb, calleePlain), out var calleeId))
                {
                    var callProps = new Dictionary<string, object> { ["caller"] = r.ObjectName, ["callee"] = calleeId, ["kind"] = "EXEC" };
                    if (isCrossDbCall)
                    {
                        callProps["is_cross_database"] = true;
                        callProps["source_database"] = db;
                        callProps["target_database"] = SplitName(calleeId).db;
                    }
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "CALLS",
                        StartNodeId = r.ObjectName,
                        EndNodeId = calleeId,
                        Properties = callProps,
                    });
                }
            }

            // ── CALLS: scalar/table-valued function invocations resolved to a
            // known SQL_SCALAR_FUNCTION / SQL_TABLE_VALUED_FUNCTION object ─────
            foreach (var callee in r.FunctionCalls)
            {
                var (calleeDb, calleePlain, isCrossDbCall) = ResolveCalleeKey(callee, db);
                // See the ExecCalls loop above: self-calls are real CALLS edges too.
                if (byPlainName.TryGetValue((calleeDb, calleePlain), out var calleeId))
                {
                    var callProps = new Dictionary<string, object> { ["caller"] = r.ObjectName, ["callee"] = calleeId, ["kind"] = "FUNCTION" };
                    if (isCrossDbCall)
                    {
                        callProps["is_cross_database"] = true;
                        callProps["source_database"] = db;
                        callProps["target_database"] = SplitName(calleeId).db;
                    }
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "CALLS",
                        StartNodeId = r.ObjectName,
                        EndNodeId = calleeId,
                        Properties = callProps,
                    });
                }
            }
        }

        // ── AFFECTS: transitive impact through CALLS chains ─────────────────
        // If A calls B (directly or via a chain of CALLS, EXEC or FUNCTION) and B
        // writes to table T, then A transitively affects T - even though A's own
        // FlowLinks never mention T. This is what answers "if I change @X here,
        // which table further down the call chain ends up modified?".
        var directWrites = new Dictionary<string, HashSet<string>>();
        foreach (var rel in graph.Relationships.Where(rel => rel.Type == "WRITES_TO").ToList())
        {
            var objId = rel.StartNodeId[..rel.StartNodeId.IndexOf("#step", StringComparison.Ordinal)];
            if (!directWrites.TryGetValue(objId, out var set))
                directWrites[objId] = set = new HashSet<string>();
            set.Add(rel.EndNodeId);
        }

        var callGraph = new Dictionary<string, List<string>>();
        foreach (var rel in graph.Relationships.Where(rel => rel.Type == "CALLS").ToList())
        {
            if (!callGraph.TryGetValue(rel.StartNodeId, out var list))
                callGraph[rel.StartNodeId] = list = new List<string>();
            list.Add(rel.EndNodeId);
        }

        var affectsSeen = new HashSet<(string obj, string table)>();
        foreach (var objId in objectIds)
        {
            var visited = new HashSet<string> { objId };
            var queue = new Queue<(string node, int hops)>();
            if (callGraph.TryGetValue(objId, out var direct))
                foreach (var c in direct)
                    queue.Enqueue((c, 1));

            while (queue.Count > 0)
            {
                var (node, hops) = queue.Dequeue();
                if (!visited.Add(node))
                    continue;

                if (directWrites.TryGetValue(node, out var tables))
                {
                    foreach (var tableId in tables)
                    {
                        if (affectsSeen.Add((objId, tableId)))
                        {
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = "AFFECTS",
                                StartNodeId = objId,
                                EndNodeId = tableId,
                                Properties = { ["via"] = node, ["hops"] = hops },
                            });
                        }
                    }
                }

                if (callGraph.TryGetValue(node, out var next))
                    foreach (var n in next)
                        if (!visited.Contains(n))
                            queue.Enqueue((n, hops + 1));
            }
        }

        // ── Workflow nodes: one per (database, schema) pair, grouping all Process
        // nodes in the same schema under a common container. A Workflow node
        // represents a business domain / pipeline (e.g. all procs in "dbo" of
        // "DWH_Pro" belong to the same workflow). Each Process gets a BELONGS_TO
        // edge pointing to its Workflow. The Workflow carries the count of its
        // processes (process_count) and the set of tables it collectively writes
        // to (aggregated from the WRITES_TO / AFFECTS edges of its members).
        var wfIds = new Dictionary<(string db, string schema), string>(
            comparer: EqualityComparer<(string, string)>.Default);
        var wfCount = new Dictionary<(string db, string schema), int>();

        // First pass: count processes per workflow key.
        foreach (var r in results)
        {
            var (rDb, rPlain) = SplitName(r.ObjectName);
            var (rSchema, _) = SplitSchemaObject(rPlain);
            var wfKey = (rDb, rSchema.ToLowerInvariant());
            wfCount[wfKey] = wfCount.GetValueOrDefault(wfKey) + 1;
        }

        // Second pass: create Workflow nodes + BELONGS_TO edges.
        foreach (var r in results)
        {
            var (rDb, rPlain) = SplitName(r.ObjectName);
            var (rSchema, _) = SplitSchemaObject(rPlain);
            var wfKey = (rDb, rSchema.ToLowerInvariant());

            if (!wfIds.TryGetValue(wfKey, out var wfId))
            {
                wfId = $"wf:{rDb}:{rSchema.ToLowerInvariant()}";
                wfIds[wfKey] = wfId;
                graph.Nodes.Add(new GraphNode
                {
                    Id = wfId,
                    Labels = new List<string> { "Workflow" },
                    Properties = new Dictionary<string, object>
                    {
                        ["name"] = rSchema,
                        ["database"] = rDb,
                        ["schema"] = rSchema,
                        ["process_count"] = wfCount[wfKey],
                    },
                });
            }

            graph.Relationships.Add(new GraphRel
            {
                Type = "BELONGS_TO",
                StartNodeId = r.ObjectName,
                EndNodeId = wfId,
                Properties = { ["object_type"] = r.ObjectType },
            });
        }

        // ── WORKFLOW_WRITES_TO: roll up WRITES_TO from Steps to the Workflow so
        // "which tables does this workflow touch?" is a single hop in the graph.
        // ToList() materializes the filtered set before we add new relationships.
        var wfWritesSeen = new HashSet<(string wf, string table)>();
        foreach (var rel in graph.Relationships.Where(r => r.Type == "WRITES_TO").ToList())
        {
            var ownerObjId = rel.StartNodeId.Contains("#step", StringComparison.Ordinal)
                ? rel.StartNodeId[..rel.StartNodeId.IndexOf("#step", StringComparison.Ordinal)]
                : null;
            if (ownerObjId == null)
                continue;
            var (oDb, oPlain) = SplitName(ownerObjId);
            var (oSchema, _) = SplitSchemaObject(oPlain);
            var wfKey = (oDb, oSchema.ToLowerInvariant());
            if (!wfIds.TryGetValue(wfKey, out var wfId))
                continue;
            if (wfWritesSeen.Add((wfId, rel.EndNodeId)))
            {
                graph.Relationships.Add(new GraphRel
                {
                    Type = "WORKFLOW_WRITES_TO",
                    StartNodeId = wfId,
                    EndNodeId = rel.EndNodeId,
                });
            }
        }

        // ── :Trigger nodes for triggers an object CREATEs in its body (typically via
        // resolved dynamic SQL - see SqlAnalyzer.ExtractTriggerCreation). Modeled as
        // first-class :SqlObject nodes, deliberately DECOUPLED
        // from the creating proc: the proc CREATES the trigger, but the trigger's own body
        // runs later, when the base table is modified - so its writes must NOT be attributed
        // to the proc. This is the "what trigger, on whom, when" layer; the body's lineage is
        // a later phase. See docs/dynamic-trigger-modeling-spec.md.
        var triggerNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var createsSeen = new HashSet<(string, string)>();
        foreach (var r in results)
        {
            if (r.CreatedTriggers.Count == 0)
                continue;
            var (db, _) = SplitName(r.ObjectName);
            foreach (var trig in r.CreatedTriggers)
            {
                var plain = SqlText.StripBrackets(trig.TriggerName);
                if (plain.Length == 0)
                    continue;
                var triggerId = $"{db}::{plain}";
                if (triggerNodeIds.Add(triggerId))
                {
                    var (schemaName, shortName) = SplitSchemaObject(plain);
                    graph.Nodes.Add(new GraphNode
                    {
                        Id = triggerId,
                        Labels = new List<string> { "SqlObject", "Trigger" },
                        Properties = new Dictionary<string, object>
                        {
                            ["database"] = db,
                            ["schema"] = schemaName,
                            ["name"] = shortName,
                            ["full_name"] = plain,
                            ["object_type"] = "TRIGGER",
                            // After / InsteadOf / For, and the DML events it fires on.
                            ["trigger_timing"] = trig.Timing,
                            ["trigger_events"] = trig.Events,
                            // No source file: it only exists as text built at runtime.
                            ["is_dynamically_created"] = true,
                        },
                    });
                    // Trigger -[:ON]-> the table whose INSERT/UPDATE/DELETE fires it.
                    // De-bracket so the Table node's `name` is clean "Schema.Table" and
                    // dedups with the same table however another step referenced it.
                    var onTableRaw = SqlText.StripBrackets(trig.OnTable);
                    var (onTableId, _) = GetOrCreateTable(graph, tableIds, tableShortNames,db, onTableRaw);
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "ON",
                        StartNodeId = triggerId,
                        EndNodeId = onTableId,
                        Properties = new Dictionary<string, object>
                        {
                            ["events"] = trig.Events,
                            ["timing"] = trig.Timing,
                        },
                    });
                }
                // proc -[:CREATES]-> Trigger (one per creating object; DDL, not runtime nav).
                if (createsSeen.Add((r.ObjectName, triggerId)))
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "CREATES",
                        StartNodeId = r.ObjectName,
                        EndNodeId = triggerId,
                        Properties = new Dictionary<string, object> { ["line_no"] = trig.LineNo },
                    });
            }
        }

        // ── Synonym resolution ────────────────────────────────────────────────
        // A CREATE SYNONYM makes `dbo.synOrders` an alias for a real object. Without
        // resolution a reader of the synonym points at a phantom :Table node distinct
        // from the base table, so impact analysis is split between the alias and its
        // target ("who reads Orders?" would miss readers that go through the synonym).
        // Redirect every edge (and column) referencing a synonym's table node onto the
        // base object's table node, drop the now-orphan synonym node, and record the
        // alias as a documentary ALIAS_OF edge from the synonym's SqlObject.
        ResolveSynonyms(graph, results, tableIds, tableShortNames);

        // Promote the SQL containment hierarchy (Database -> Schema -> Object/Table)
        // to real nodes, so "everything in schema Sales" / "impact across this database"
        // is a graph traversal instead of string-parsing the `database`/`schema`
        // properties. Runs last, over the finished node set, so it catches every
        // SqlObject and Table (including the view/derived tables added late).
        BuildContainmentHierarchy(graph);

        // Assign a stable sequential id to every relationship so consumers can
        // unambiguously reference individual edges (especially when a single Step
        // has multiple READS_FROM/WRITES_TO edges for multi-table operations).
        for (int i = 0; i < graph.Relationships.Count; i++)
            graph.Relationships[i].Id = $"r{i}";

        return graph;
    }

    /// <summary>
    /// Adds :Database and :Schema nodes plus CONTAINS edges
    /// (Database -[:CONTAINS]-> Schema -[:CONTAINS]-> SqlObject/Table), derived from the
    /// database/schema of every existing SqlObject and Table. Both are *shared* nodes
    /// (deduped globally): a schema/database is referenced by many objects. Column nodes
    /// keep their table-scheme ids untouched - this only adds the upper containment
    /// layer, so existing READS_COLUMN/DERIVES_FROM stay valid.
    /// </summary>
    private static void BuildContainmentHierarchy(GraphPayload graph)
    {
        var dbIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var schemaIds = new Dictionary<(string db, string schema), string>();
        var containsSeen = new HashSet<(string from, string to)>();

        void AddContains(string from, string to)
        {
            if (containsSeen.Add((from, to)))
                graph.Relationships.Add(new GraphRel { Type = "CONTAINS", StartNodeId = from, EndNodeId = to });
        }

        string EnsureDatabase(string db)
        {
            if (!dbIds.TryGetValue(db, out var id))
            {
                id = $"{db}:database";
                dbIds[db] = id;
                graph.Nodes.Add(new GraphNode
                {
                    Id = id,
                    Labels = new List<string> { "Database" },
                    Properties = new Dictionary<string, object> { ["name"] = db },
                });
            }
            return id;
        }

        string EnsureSchema(string db, string schema)
        {
            // Key by lower-cased schema: a SqlObject carries it original-case ("Application")
            // while a Table carries it normalized-lower ("application"); without this they'd
            // mint two :Schema nodes with the same id. Display name keeps first-seen case
            // (SqlObjects are emitted before Tables, so original case wins).
            var key = (db: db, schema: schema.ToLowerInvariant());
            if (!schemaIds.TryGetValue(key, out var id))
            {
                id = $"{db}:schema:{key.schema}";
                schemaIds[key] = id;
                graph.Nodes.Add(new GraphNode
                {
                    Id = id,
                    Labels = new List<string> { "Schema" },
                    Properties = new Dictionary<string, object> { ["database"] = db, ["name"] = schema },
                });
                AddContains(EnsureDatabase(db), id);
            }
            return id;
        }

        // Snapshot first: EnsureDatabase/EnsureSchema append to graph.Nodes while we iterate.
        foreach (var n in graph.Nodes.ToList())
        {
            bool isObject = n.Labels.Contains("SqlObject");
            bool isTable = n.Labels.Contains("Table");
            if (!isObject && !isTable)
                continue;

            var db = n.Properties.TryGetValue("database", out var d) ? d?.ToString() ?? "" : "";
            if (db.Length == 0)
                continue;

            string schema;
            if (isObject && n.Properties.TryGetValue("schema", out var s))
                schema = s?.ToString() ?? "dbo";
            else
                // Table "name" is "schema.table" (or just "table" -> dbo).
                (schema, _) = SplitSchemaObject(NormalizeRef(n.Properties.TryGetValue("name", out var nm) ? nm?.ToString() ?? "" : ""));

            if (schema.Length == 0)
                schema = "dbo";

            AddContains(EnsureSchema(db, schema), n.Id);
        }
    }

    /// <summary>
    /// Pass 1: emits :Table nodes (always) and, when includeColumns is true,
    /// (:Table)-[:HAS_COLUMN {ordinal}]->(:Column {data_type, is_nullable, ...}) for every CREATE TABLE.
    /// Pass 2 (after all tables/columns exist): for each FOREIGN KEY, adds
    /// (:Table)-[:FK_TO {constraint}]->(:Table) (always) and, when includeColumns,
    /// (:Column)-[:REFERENCES {constraint}]->(:Column) per column pair.
    /// </summary>
    private static void BuildTableSchemas(
        GraphPayload graph,
        List<TableSchemaResult> tableSchemas,
        Dictionary<(string db, string name), string> tableIds,
        Dictionary<(string db, string shortName), string?> tableShortNames,
        Dictionary<(string tableId, string column), string> columnIds,
        bool includeColumns)
    {
        foreach (var schema in tableSchemas)
        {
            if (schema.Error != null)
                continue;

            var (db, plain) = SplitName(schema.ObjectName);
            var tableKey = (db, NormalizeRef(plain));
            if (!tableIds.TryGetValue(tableKey, out var tableId))
            {
                tableId = $"{db}:table:{tableKey.Item2}";
                tableIds[tableKey] = tableId;
                RegisterShortName(tableShortNames, db, tableKey.Item2);
                graph.Nodes.Add(new GraphNode
                {
                    Id = tableId,
                    Labels = new List<string> { "Table" },
                    Properties = new Dictionary<string, object>
                    {
                        ["database"] = db,
                        // De-bracket for display; NormalizeRef (via tableKey) already
                        // strips brackets for the id, but also lowercases, which would
                        // clobber casing in the display name.
                        ["name"] = SqlText.StripBrackets(plain),
                    },
                });
            }

            if (includeColumns)
            {
                foreach (var col in schema.Columns)
                {
                    var colKey = (tableId, col.Name.ToLowerInvariant());
                    if (columnIds.ContainsKey(colKey))
                        continue;

                    var colId = $"{tableId}:column:{col.Name}";
                    columnIds[colKey] = colId;
                    graph.Nodes.Add(new GraphNode
                    {
                        Id = colId,
                        Labels = new List<string> { "Column" },
                        Properties = new Dictionary<string, object>
                        {
                            ["name"] = col.Name,
                            ["table"] = tableKey.Item2,
                            ["data_type"] = col.DataType,
                            ["is_nullable"] = col.IsNullable,
                            ["is_identity"] = col.IsIdentity,
                            ["is_primary_key"] = col.IsPrimaryKey,
                            ["ordinal"] = col.Ordinal,
                        },
                    });
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "HAS_COLUMN",
                        StartNodeId = tableId,
                        EndNodeId = colId,
                        Properties = { ["ordinal"] = col.Ordinal },
                    });
                }

                // Declarative DDL constraints (CHECK/DEFAULT/UNIQUE) as :BusinessRule nodes:
                // HAS_RULE from the table, CONSTRAINS to each governed column. These carry
                // semantic intent ("Qty > 0") that an impact query needs but a column
                // attribute can't express. PK/FK/NOT NULL stay as attributes/FK edges.
                foreach (var con in schema.Constraints)
                {
                    var ruleId = $"{db}:bizrule:{tableKey.Item2}:{con.Kind}:{StableHash(con.Expression + "|" + string.Join(",", con.Columns))}";
                    if (graph.Nodes.All(n => n.Id != ruleId))
                    {
                        graph.Nodes.Add(new GraphNode
                        {
                            Id = ruleId,
                            Labels = new List<string> { "BusinessRule" },
                            Properties = new Dictionary<string, object>
                            {
                                ["kind"] = con.Kind,
                                ["expression"] = con.Expression,
                                ["name"] = con.Name ?? "",
                                ["table"] = tableKey.Item2,
                            },
                        });
                        graph.Relationships.Add(new GraphRel { Type = "HAS_RULE", StartNodeId = tableId, EndNodeId = ruleId });
                    }
                    foreach (var colName in con.Columns)
                    {
                        if (columnIds.TryGetValue((tableId, colName.ToLowerInvariant()), out var govColId))
                            graph.Relationships.Add(new GraphRel { Type = "CONSTRAINS", StartNodeId = ruleId, EndNodeId = govColId });
                    }
                }
            }
        }

        // Pass 2: FK relationships. FK_TO (table-table) is always emitted so the
        // ER lineage is available even without --columns. REFERENCES (column-column)
        // is only emitted when includeColumns (column nodes must exist first).
        foreach (var schema in tableSchemas)
        {
            if (schema.Error != null || schema.ForeignKeys.Count == 0)
                continue;

            var (db, plain) = SplitName(schema.ObjectName);
            var tableKey = (db, NormalizeRef(plain));
            if (!tableIds.TryGetValue(tableKey, out var tableId))
                continue;

            // "REFERENCES Customers" (no schema) -> assume the FK table's own schema.
            var sourceSchema = plain.Contains('.') ? plain[..plain.IndexOf('.')] : "";

            foreach (var fk in schema.ForeignKeys)
            {
                var refName = NormalizeRef(fk.ReferencedTable);
                if (!refName.Contains('.') && sourceSchema.Length > 0)
                    refName = $"{NormalizeRef(sourceSchema)}.{refName}";

                if (!tableIds.TryGetValue((db, refName), out var refTableId))
                    continue; // referenced table has no known schema - skip

                graph.Relationships.Add(new GraphRel
                {
                    Type = "FK_TO",
                    StartNodeId = tableId,
                    EndNodeId = refTableId,
                    Properties = { ["constraint"] = fk.ConstraintName ?? "" },
                });

                if (includeColumns)
                {
                    for (int i = 0; i < fk.Columns.Count && i < fk.ReferencedColumns.Count; i++)
                    {
                        var colKey = (tableId, fk.Columns[i].ToLowerInvariant());
                        var refColKey = (refTableId, fk.ReferencedColumns[i].ToLowerInvariant());
                        if (columnIds.TryGetValue(colKey, out var colId) && columnIds.TryGetValue(refColKey, out var refColId))
                        {
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = "REFERENCES",
                                StartNodeId = colId,
                                EndNodeId = refColId,
                                Properties = { ["constraint"] = fk.ConstraintName ?? "" },
                            });
                        }
                    }
                }
            }
        }

        // Pass 3: computed columns ("Total AS Price * Qty"). DERIVES_FROM from the
        // computed column to each other column of the same table its expression
        // reads - same edge type/shape as INSERT...SELECT lineage (GraphExporter
        // above), so a query walking DERIVES_FROM doesn't need a special case for
        // "this column is derived via DDL vs. via a procedure's data flow".
        if (includeColumns)
        {
            foreach (var schema in tableSchemas)
            {
                if (schema.Error != null)
                    continue;

                var (db, plain) = SplitName(schema.ObjectName);
                var tableKey = (db, NormalizeRef(plain));
                if (!tableIds.TryGetValue(tableKey, out var tableId))
                    continue;

                foreach (var col in schema.Columns)
                {
                    if (col.ComputedSourceColumns.Count == 0)
                        continue;

                    if (!columnIds.TryGetValue((tableId, col.Name.ToLowerInvariant()), out var colId))
                        continue;

                    foreach (var srcCol in col.ComputedSourceColumns)
                    {
                        if (!columnIds.TryGetValue((tableId, srcCol.ToLowerInvariant()), out var srcColId))
                            continue;

                        var computedProps = new Dictionary<string, object>
                        {
                            ["logic"] = col.ComputedExpression,
                            ["via_computed_column"] = true,
                        };
                        AddOpKinds(computedProps, col.ComputedOpKinds);
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "DERIVES_FROM",
                            StartNodeId = colId,
                            EndNodeId = srcColId,
                            Properties = computedProps,
                        });
                    }
                }
            }
        }

    }

    /// <summary>
    /// View output-column lineage ("CREATE VIEW v AS SELECT a.X+a.Y AS Total ..").
    /// DERIVES_FROM from the view's own :Column (Total) to each base table column it
    /// reads (a.X, a.Y) - same edge shape as INSERT...SELECT/computed columns, so a view
    /// becomes a transparent lineage hop, tagged via_view so a reader can tell it apart.
    /// </summary>
    private static void BuildViewLineage(GraphPayload graph, List<ObjectResult> results,
        Dictionary<(string db, string name), string> tableIds,
        Dictionary<(string db, string shortName), string?> tableShortNames,
        Dictionary<(string tableId, string column), string> columnIds)
    {
        foreach (var r in results)
        {
            if (r.ViewColumnLineage.Count == 0)
                continue;

            var (db, plain) = SplitName(r.ObjectName);
            var (viewTableId, viewTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, plain);

            // The view's output columns keep the table-scheme id (":table:<view>:column:<c>")
            // so a downstream "SELECT c FROM <view>" reader's READS_COLUMN lands on the
            // same node - lineage stays continuous. But that id hangs the column off a
            // :Table node that's disconnected from the view's :SqlObject, leaving the
            // object's output columns unreachable from the object itself. Link them back
            // with HAS_COLUMN from the SqlObject (deduped) so "the columns of this view"
            // is answerable from the view node, not just from its phantom table twin.
            var ownedCols = new HashSet<string>(StringComparer.Ordinal);

            foreach (var deriv in r.ViewColumnLineage)
            {
                if (IsTempOrVariable(deriv.SourceTable))
                    continue;

                var outColId = GetOrCreateColumn(graph, columnIds, viewTableId, viewTableName, deriv.TargetColumn);
                if (ownedCols.Add(outColId))
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "HAS_COLUMN",
                        StartNodeId = r.ObjectName,
                        EndNodeId = outColId,
                    });
                var (srcTableId, srcTableName) = GetOrCreateTable(graph, tableIds, tableShortNames,db, deriv.SourceTable);
                foreach (var srcCol in deriv.SourceColumns)
                {
                    var srcColId = GetOrCreateColumn(graph, columnIds, srcTableId, srcTableName, srcCol);
                    var viewProps = new Dictionary<string, object>
                    {
                        ["logic"] = deriv.TransformationExpression,
                        ["via_view"] = true,
                    };
                    AddOpKinds(viewProps, deriv.OpKinds);
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "DERIVES_FROM",
                        StartNodeId = outColId,
                        EndNodeId = srcColId,
                        Properties = viewProps,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Adds the structured operator tokens (see <see cref="OperatorClassifier"/>) to an
    /// edge's properties as "op_kinds", but only when there are any - a plain column copy
    /// ("INSERT T(a) SELECT a") or operator-free predicate leaves the edge uncluttered.
    /// </summary>
    private static void AddOpKinds(Dictionary<string, object> props, IReadOnlyList<string> ops)
    {
        if (ops.Count > 0)
            props["op_kinds"] = ops;
    }

    /// <summary>Returns the (possibly newly-created) :Table node for "db.tableName", de-duplicated via tableIds.</summary>
    /// <summary>
    /// Resolves CREATE SYNONYM aliases: redirects every relationship (and column node)
    /// that points at a synonym's :Table node onto the base object's :Table node, so the
    /// impact graph treats a read/write through the synonym as a read/write of the real
    /// table. Drops the orphan synonym :Table node and adds an ALIAS_OF edge from the
    /// synonym's SqlObject to the base table for documentation. Identical edges created by
    /// the redirect are de-duplicated. No-op when there are no synonyms.
    /// </summary>
    private static void ResolveSynonyms(GraphPayload graph, List<ObjectResult> results, Dictionary<(string db, string name), string> tableIds,
        Dictionary<(string db, string shortName), string?> tableShortNames)
    {
        var synonyms = results.Where(r => r.ObjectType == "SYNONYM" && r.SynonymTarget.Length > 0).ToList();
        if (synonyms.Count == 0)
            return;

        // Build a single id-remap: each synonym's :Table node (and its column nodes) -> the
        // base object's :Table node (and matching base columns). "<synTableId>" -> "<baseTableId>",
        // "<synTableId>:column:X" -> "<baseTableId>:column:X".
        var tableRemap = new Dictionary<string, string>();       // synTableId -> baseTableId
        var aliasEdges = new List<GraphRel>();
        foreach (var syn in synonyms)
        {
            var (synDb, synPlain) = SplitName(syn.ObjectName);
            var synTableId = $"{synDb}:table:{NormalizeRef(synPlain)}";
            var (baseTableId, _) = GetOrCreateTable(graph, tableIds, tableShortNames,synDb, syn.SynonymTarget);
            if (synTableId == baseTableId || tableRemap.ContainsKey(synTableId))
                continue;
            tableRemap[synTableId] = baseTableId;
            if (graph.Nodes.Any(n => n.Id == syn.ObjectName))
                aliasEdges.Add(new GraphRel
                {
                    Type = "ALIAS_OF",
                    StartNodeId = syn.ObjectName,
                    EndNodeId = baseTableId,
                    Properties = new Dictionary<string, object> { ["target"] = syn.SynonymTarget },
                });
        }
        if (tableRemap.Count == 0)
            return;

        string Remap(string id)
        {
            if (tableRemap.TryGetValue(id, out var baseId))
                return baseId;
            var colIdx = id.IndexOf(":column:", StringComparison.Ordinal);
            if (colIdx > 0 && tableRemap.TryGetValue(id.Substring(0, colIdx), out var baseTbl))
                return baseTbl + id.Substring(colIdx);
            return id;
        }

        // GraphRel is init-only, so rebuild the list with remapped endpoints instead of
        // mutating in place. De-duplicate exact (Type, Start, End) edges the redirect can
        // create (e.g. two HAS_COLUMN to the same base column); distinct Steps reading the
        // same table keep their own edges (different StartNodeId), so nothing real is lost.
        var rebuilt = new List<GraphRel>(graph.Relationships.Count);
        var seen = new HashSet<(string, string, string)>();
        foreach (var rel in graph.Relationships.Concat(aliasEdges))
        {
            var start = Remap(rel.StartNodeId);
            var end = Remap(rel.EndNodeId);
            if (!seen.Add((rel.Type, start, end)))
                continue;
            rebuilt.Add(start == rel.StartNodeId && end == rel.EndNodeId
                ? rel
                : new GraphRel { Type = rel.Type, StartNodeId = start, EndNodeId = end, Properties = rel.Properties });
        }
        graph.Relationships.Clear();
        graph.Relationships.AddRange(rebuilt);

        // Drop each synonym's now-orphan :Table node and its column nodes (edges point at base).
        graph.Nodes.RemoveAll(n =>
            tableRemap.ContainsKey(n.Id) ||
            (n.Id.IndexOf(":column:", StringComparison.Ordinal) is var ci && ci > 0 && tableRemap.ContainsKey(n.Id.Substring(0, ci))));
    }

    private static (string tableId, string tableName) GetOrCreateTable(
        GraphPayload graph,
        Dictionary<(string db, string name), string> tableIds,
        Dictionary<(string db, string shortName), string?> tableShortNames,
        string db, string tableName)
    {
        var normalized = NormalizeRef(tableName);
        var tableKey = (db, normalized);
        if (!tableIds.TryGetValue(tableKey, out var tableId))
        {
            // Unqualified reference ("QueueDatabase") that matches exactly one already-
            // known qualified table ("dbo.QueueDatabase" - registered via its CREATE
            // TABLE schema, or a prior qualified reference elsewhere in this same run):
            // resolve to that table's existing node instead of minting a same-table
            // twin that silently splits its READS_FROM/WRITES_TO edges across two
            // :Table nodes (the false negative this method exists to prevent).
            if (!normalized.Contains('.') &&
                tableShortNames.TryGetValue((db, normalized), out var qualifiedName) &&
                qualifiedName != null &&
                tableIds.TryGetValue((db, qualifiedName), out var existingId))
            {
                tableIds[tableKey] = existingId;
                return (existingId, qualifiedName);
            }

            tableId = $"{db}:table:{tableKey.Item2}";
            tableIds[tableKey] = tableId;
            graph.Nodes.Add(new GraphNode
            {
                Id = tableId,
                Labels = new List<string> { "Table" },
                Properties = new Dictionary<string, object>
                {
                    ["database"] = db,
                    // De-bracket for display (NormalizeRef, used for tableKey/id, also
                    // lowercases - wrong for a display name's casing).
                    ["name"] = SqlText.StripBrackets(tableName),
                },
            });
            RegisterShortName(tableShortNames, db, normalized);
        }
        return (tableId, tableKey.Item2);
    }

    /// <summary>
    /// Indexes a newly-registered qualified table ("dbo.Foo") under its bare short name
    /// ("Foo") so a later unqualified reference can find it (see GetOrCreateTable). If a
    /// *different* qualified table already claims that short name (e.g. "dbo.Foo" and
    /// "archive.Foo" both registered), the short name is marked ambiguous (null) so the
    /// shortcut is disabled rather than guessing which one an unqualified reference means.
    /// A bare table name with no schema segment ("Foo" itself, never seen qualified)
    /// registers as its own short name - harmless no-op for the lookup.
    /// </summary>
    private static void RegisterShortName(Dictionary<(string db, string shortName), string?> tableShortNames, string db, string normalizedName)
    {
        var dot = normalizedName.LastIndexOf('.');
        var shortName = dot >= 0 ? normalizedName[(dot + 1)..] : normalizedName;
        var shortKey = (db, shortName);
        if (tableShortNames.TryGetValue(shortKey, out var existing))
        {
            if (existing != null && existing != normalizedName)
                tableShortNames[shortKey] = null;
        }
        else
        {
            tableShortNames[shortKey] = normalizedName;
        }
    }

    /// <summary>Returns the (possibly newly-created) :Column node "tableId:column:colName", de-duplicated via columnIds, with HAS_COLUMN linked to its table.</summary>
    private static string GetOrCreateColumn(GraphPayload graph, Dictionary<(string tableId, string column), string> columnIds, string tableId, string tableName, string colName)
    {
        var colKey = (tableId, colName.ToLowerInvariant());
        if (!columnIds.TryGetValue(colKey, out var colId))
        {
            colId = $"{tableId}:column:{colName}";
            columnIds[colKey] = colId;
            graph.Nodes.Add(new GraphNode
            {
                Id = colId,
                Labels = new List<string> { "Column" },
                Properties = new Dictionary<string, object>
                {
                    ["name"] = colName,
                    ["table"] = tableName,
                },
            });
            graph.Relationships.Add(new GraphRel
            {
                Type = "HAS_COLUMN",
                StartNodeId = tableId,
                EndNodeId = colId,
            });
        }
        return colId;
    }

    /// <summary>Splits "Database::Schema.Object" into ("Database", "Schema.Object").</summary>
    private static (string db, string plain) SplitName(string objectName)
    {
        var parts = objectName.Split("::", 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("", objectName);
    }

    /// <summary>Strips brackets/whitespace so "[Schema].[Object]" matches "Schema.Object".</summary>
    private static string NormalizeRef(string raw) => SqlText.NormalizeRef(raw);

    /// <summary>
    /// PROTOTYPE (dynsql-placeholder): matches the "«param:@Name»" token AstWalker
    /// substitutes for an unresolvable identifier wrapped in QUOTENAME(...) (see
    /// ResolveLiteral). Detected here, at edge-construction time, so a table/schema/db
    /// name reconstructed through this substitution never produces an edge that looks as
    /// certain as one built from real static text.
    /// </summary>
    private static readonly Regex DynamicSqlPlaceholderPattern = new(@"«param:(@\w+)»", RegexOptions.Compiled);

    /// <summary>
    /// PROTOTYPE (dynsql-placeholder): if <paramref name="reconstructedName"/> (a table
    /// name reconstructed from resolved dynamic SQL) contains the placeholder token,
    /// marks <paramref name="relProps"/> as an INFERRED edge rather than a certain one:
    /// confidence &lt; 1.0 (reusing PlanEnricher's confidence property - 1.0 there means
    /// "confirmed by execution plan"; here, &lt;1.0 means "partially resolved statically"),
    /// an explicit inferred flag, the @parameter the missing identifier is bound to, and -
    /// when the placeholder sits in the position a 3-part name reserves for the database -
    /// an explicit database_unknown flag. No-op (relProps untouched) when the name carries
    /// no placeholder, i.e. every edge NOT produced by this substitution keeps its current,
    /// unmarked (certain) shape.
    /// </summary>
    private static void MarkIfDynamicPlaceholder(Dictionary<string, object> relProps, string reconstructedName)
    {
        var match = DynamicSqlPlaceholderPattern.Match(reconstructedName);
        if (!match.Success)
            return;

        relProps["inferred"] = true;
        relProps["confidence"] = 0.5;
        relProps["bound_to"] = match.Groups[1].Value;

        var rawParts = reconstructedName.Split('.');
        if (rawParts.Length >= 3 && DynamicSqlPlaceholderPattern.IsMatch(rawParts[0]))
            relProps["database_unknown"] = true;
    }

    /// <summary>
    /// Short, stable (cross-process, cross-run) hash for Rule node ids. Unlike
    /// string.GetHashCode() - randomized per process in .NET - this gives the same
    /// id for the same condition text every time, so the same input always
    /// produces the same graph (required for NodeStoreExporter.Update's diffing).
    /// </summary>
    private static string StableHash(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>
    /// Classifies a step into a high-level workflow step type based on the SQL
    /// operation and target, matching common ETL/process patterns:
    /// Extraction (read from source), Load (write to target), Transformation
    /// (modify/derive data), Staging (temp/variable tables), Cleanup, Orchestration,
    /// Maintenance, Transaction, Notification, Cursor, Control, or Operation.
    /// </summary>
    private static string ClassifyStepType(string consequenceType, string target)
    {
        var isTemp = IsTempOrVariable(target);
        return consequenceType switch
        {
            "INSERT" => isTemp ? "Staging" : "Load",
            "SELECT" => isTemp ? "Staging" : "Extraction",
            "UPDATE" => "Transformation",
            "DELETE" => "Cleanup",
            "MERGE" => "Load",
            "EXEC" => "Orchestration",
            "TRUNCATE" => "Cleanup",
            "ALTER" or "CREATE_TABLE" or "CREATE_INDEX" or "DROP_TABLE" => "Maintenance",
            "BEGIN_TRAN" or "COMMIT_TRAN" or "ROLLBACK" => "Transaction",
            "THROW" or "PRINT" => "Notification",
            "RETURN" or "BREAK" or "CONTINUE" or "GOTO" or "WAITFOR" => "Control",
            "OPEN_CURSOR" or "FETCH" or "CLOSE_CURSOR" or "DEALLOCATE" or "DECLARE_CURSOR" => "Cursor",
            _ => "Operation"
        };
    }

    /// <summary>True for a table variable ("@T") or local/global temp table ("#T"/"##T") reference - never emitted as a :Table node, so the Table graph only contains real persisted tables/views.</summary>
    /// <remarks>
    /// Normalizes first so bracketed ("[#BlitzResults]") and qualified ("tempdb..#Foo",
    /// "[tempdb]..[#Foo]") forms - which ScriptDom emits for quoted identifiers - are still
    /// recognized. A raw StartsWith('#') misses "[#..." entirely, which was letting bracketed
    /// temp writes leak a phantom :Table node (and skip the tempOrigin bridge).
    /// </remarks>
    private static bool IsTempOrVariable(string target)
    {
        var t = NormalizeRef(target);          // strips [ ] and lowercases
        var lastDot = t.LastIndexOf('.');      // last segment of a qualified name (tempdb..#x)
        if (lastDot >= 0)
            t = t[(lastDot + 1)..];
        return t.StartsWith('#') || t.StartsWith('@');
    }

    /// <summary>
    /// Detects cross-database table references (3-part names like "OtherDb.dbo.Table").
    /// Returns (true, targetDb) when the table belongs to a different database than
    /// currentDb; (false, currentDb) for same-database or 2-part references.
    /// </summary>
    private static (bool isCross, string targetDb) DetectCrossDb(string tableName, string currentDb)
    {
        var parts = NormalizeRef(tableName).Split('.');
        if (parts.Length >= 3)
        {
            var tableDb = parts[0];
            if (!string.Equals(tableDb, NormalizeRef(currentDb), StringComparison.OrdinalIgnoreCase))
                return (true, tableDb);
        }
        return (false, currentDb);
    }

    /// <summary>
    /// Resolves a CALLS target (EXEC/function callee, e.g. "dbo.Proc" or the
    /// cross-database "OtherDb.dbo.Proc") to the (db, plain) key byPlainName is
    /// indexed by, plus whether it crossed a database boundary. Mirrors
    /// DetectCrossDb's 3-part-name detection, but - unlike table refs, which keep
    /// their full "Db.Schema.Table" string as ConsequenceTarget - also strips the
    /// leading database part so the remaining "Schema.Object" matches the same
    /// "plain" shape byPlainName/SplitName use for every analyzed SqlObject.
    /// </summary>
    private static (string db, string plain, bool isCross) ResolveCalleeKey(string callee, string currentDb)
    {
        var normalized = NormalizeRef(callee);
        var parts = normalized.Split('.');
        var normalizedCurrentDb = NormalizeRef(currentDb);
        if (parts.Length >= 3 && !string.Equals(parts[0], normalizedCurrentDb, StringComparison.OrdinalIgnoreCase))
            return (parts[0], string.Join('.', parts.Skip(1)), true);
        return (normalizedCurrentDb, normalized, false);
    }

    /// <summary>
    /// Splits "Schema.Object" into ("Schema", "Object"). Single-part names default
    /// schema to "dbo". Used to populate the schema/name properties on SqlObject nodes.
    /// </summary>
    private static (string schema, string name) SplitSchemaObject(string plain)
    {
        var dot = plain.IndexOf('.');
        return dot > 0
            ? (plain[..dot], plain[(dot + 1)..])
            : ("dbo", plain);
    }
}
