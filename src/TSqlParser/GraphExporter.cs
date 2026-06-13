namespace TSqlParser;

public class GraphNode
{
    public required string Id { get; init; }
    public required List<string> Labels { get; init; }
    public required Dictionary<string, object> Properties { get; init; }
}

public class GraphRel
{
    public required string Type { get; init; }
    public required string StartNodeId { get; init; }
    public required string EndNodeId { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}

public class GraphPayload
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphRel> Relationships { get; } = new();
}

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

        foreach (var r in results)
        {
            var (db, plain) = SplitName(r.ObjectName);
            objectIds.Add(r.ObjectName);
            byPlainName[(db, NormalizeRef(plain))] = r.ObjectName;

            graph.Nodes.Add(new GraphNode
            {
                Id = r.ObjectName,
                Labels = new List<string> { "SqlObject" },
                Properties = new Dictionary<string, object>
                {
                    ["database"] = db,
                    ["full_name"] = plain,
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

        // ── Table schemas (CREATE TABLE): real Table/Column nodes with types,
        // nullability, identity, PK and FK relationships, processed before any
        // procedural step so the latter attach to these typed Column nodes.
        if (includeColumns && tableSchemas is { Count: > 0 })
            BuildTableSchemas(graph, tableSchemas, tableIds, columnIds);

        foreach (var r in results)
        {
            var (db, _) = SplitName(r.ObjectName);

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

                    var (srcTableId, srcTableName) = GetOrCreateTable(graph, tableIds, db, va.SourceTable);
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
                        ["line_no"] = fl.LineNo,
                        ["nesting_level"] = fl.NestingLevel,
                        ["action"] = fl.ConsequenceType,
                        ["target_name"] = fl.ConsequenceTarget,
                        ["detail"] = fl.Detail,
                        ["dynamic_sql"] = fl.DynamicSqlText,
                        ["is_dynamic_sql"] = fl.DynamicSqlVars.Count > 0,
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

                // Target resolution: either another analyzed SqlObject (proc/function/
                // view/trigger - e.g. EXEC of a sibling proc, or INSERT...SELECT off a
                // TVF) or, far more commonly, a plain data Table that was never itself
                // analyzed. Both get their own node so a reader (or query) can tell
                // "this Step touches PROCEDURE X" apart from "this Step touches TABLE Y"
                // without guessing from the name.
                if (fl.ConsequenceTarget.Length > 0 &&
                    byPlainName.TryGetValue((db, NormalizeRef(fl.ConsequenceTarget)), out var targetObjId))
                {
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "TARGETS",
                        StartNodeId = stepId,
                        EndNodeId = targetObjId,
                    });
                }
                else if (fl.ConsequenceTarget.Length > 0 && fl.ConsequenceType is "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "SELECT" or "ALTER")
                {
                    var (tableId, tableName) = GetOrCreateTable(graph, tableIds, db, fl.ConsequenceTarget);
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = fl.ConsequenceType == "SELECT" ? "READS_FROM" : "WRITES_TO",
                        StartNodeId = stepId,
                        EndNodeId = tableId,
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
                            graph.Relationships.Add(new GraphRel
                            {
                                Type = colRelType,
                                StartNodeId = stepId,
                                EndNodeId = colId,
                            });
                        }

                        // DERIVES_FROM: for "INSERT INTO T (...) SELECT ... FROM S ...",
                        // (:Column T.Col)-[:DERIVES_FROM]->(:Column S.SrcCol) per target
                        // column whose value was positionally traced back to source
                        // column(s) of S - column-to-column lineage for ETL-style procs.
                        foreach (var deriv in fl.ColumnLineage)
                        {
                            var targetColId = GetOrCreateColumn(graph, columnIds, tableId, tableName, deriv.TargetColumn);
                            var (srcTableId, srcTableName) = GetOrCreateTable(graph, tableIds, db, deriv.SourceTable);
                            foreach (var srcCol in deriv.SourceColumns)
                            {
                                var srcColId = GetOrCreateColumn(graph, columnIds, srcTableId, srcTableName, srcCol);
                                graph.Relationships.Add(new GraphRel
                                {
                                    Type = "DERIVES_FROM",
                                    StartNodeId = targetColId,
                                    EndNodeId = srcColId,
                                });
                            }
                        }
                    }

                    // ExtraReads: "SELECT ... FROM A JOIN B ..." also reads from B
                    // (and any further JOIN partners) - each gets its own READS_FROM,
                    // plus READS_COLUMN for the columns of B referenced via "b.Col".
                    foreach (var extra in fl.ExtraReads)
                    {
                        var (extraTableId, extraTableName) = GetOrCreateTable(graph, tableIds, db, extra.Table);
                        graph.Relationships.Add(new GraphRel
                        {
                            Type = "READS_FROM",
                            StartNodeId = stepId,
                            EndNodeId = extraTableId,
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
                                });
                            }
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
                            ruleId = $"{db}:rule:{condType}:{condText.GetHashCode():x}";
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

            // ── CALLS: caller -> callee, resolved within the same database ─
            foreach (var callee in r.ExecCalls)
            {
                if (byPlainName.TryGetValue((db, NormalizeRef(callee)), out var calleeId) && calleeId != r.ObjectName)
                {
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "CALLS",
                        StartNodeId = r.ObjectName,
                        EndNodeId = calleeId,
                        Properties = { ["caller"] = r.ObjectName, ["callee"] = calleeId, ["kind"] = "EXEC" },
                    });
                }
            }

            // ── CALLS: scalar/table-valued function invocations resolved to a
            // known SQL_SCALAR_FUNCTION / SQL_TABLE_VALUED_FUNCTION object ─────
            foreach (var callee in r.FunctionCalls)
            {
                if (byPlainName.TryGetValue((db, NormalizeRef(callee)), out var calleeId) && calleeId != r.ObjectName)
                {
                    graph.Relationships.Add(new GraphRel
                    {
                        Type = "CALLS",
                        StartNodeId = r.ObjectName,
                        EndNodeId = calleeId,
                        Properties = { ["caller"] = r.ObjectName, ["callee"] = calleeId, ["kind"] = "FUNCTION" },
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
        foreach (var rel in graph.Relationships.Where(rel => rel.Type == "WRITES_TO"))
        {
            var objId = rel.StartNodeId[..rel.StartNodeId.IndexOf("#step", StringComparison.Ordinal)];
            if (!directWrites.TryGetValue(objId, out var set))
                directWrites[objId] = set = new HashSet<string>();
            set.Add(rel.EndNodeId);
        }

        var callGraph = new Dictionary<string, List<string>>();
        foreach (var rel in graph.Relationships.Where(rel => rel.Type == "CALLS"))
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

        return graph;
    }

    /// <summary>
    /// Pass 1: emits a typed (:Table)-[:HAS_COLUMN {ordinal}]->(:Column {data_type,
    /// is_nullable, is_identity, is_primary_key, ...}) for every CREATE TABLE.
    /// Pass 2 (after all tables/columns exist): for each FOREIGN KEY, adds
    /// (:Column)-[:REFERENCES {constraint}]->(:Column) per column pair plus a
    /// derived (:Table)-[:FK_TO {constraint}]->(:Table) - the child (many) table
    /// points to the parent (one) table, so "1 parent -> many children" reads as
    /// MATCH (child)-[:FK_TO]->(parent) in Cypher.
    /// </summary>
    private static void BuildTableSchemas(
        GraphPayload graph,
        List<TableSchemaResult> tableSchemas,
        Dictionary<(string db, string name), string> tableIds,
        Dictionary<(string tableId, string column), string> columnIds)
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
                graph.Nodes.Add(new GraphNode
                {
                    Id = tableId,
                    Labels = new List<string> { "Table" },
                    Properties = new Dictionary<string, object>
                    {
                        ["database"] = db,
                        ["name"] = plain,
                    },
                });
            }

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
        }

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

    /// <summary>Returns the (possibly newly-created) :Table node for "db.tableName", de-duplicated via tableIds.</summary>
    private static (string tableId, string tableName) GetOrCreateTable(GraphPayload graph, Dictionary<(string db, string name), string> tableIds, string db, string tableName)
    {
        var tableKey = (db, NormalizeRef(tableName));
        if (!tableIds.TryGetValue(tableKey, out var tableId))
        {
            tableId = $"{db}:table:{tableKey.Item2}";
            tableIds[tableKey] = tableId;
            graph.Nodes.Add(new GraphNode
            {
                Id = tableId,
                Labels = new List<string> { "Table" },
                Properties = new Dictionary<string, object>
                {
                    ["database"] = db,
                    ["name"] = tableName,
                },
            });
        }
        return (tableId, tableKey.Item2);
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
}
