// Transforma el graph_full.json del parser (shape Neo4j: {Nodes, Relationships})
// en el modelo del dashboard, con OBJETOS y TABLAS como entidades seleccionables.
// El grafo es superconjunto: SqlObjects, Tables, Columns, Steps, Variables, y todas
// las relaciones (CALLS, READS_FROM/WRITES_TO, FK_TO/REFERENCES, HAS_STEP, GOVERNS,
// DERIVES_FROM, ASSIGNED_FROM, BUILDS_SQL_FROM, USES_VARIABLE…). Expuesto: SD.shape.
(function (SD) {
  const last = n => (n.Labels && n.Labels.length) ? n.Labels[n.Labels.length - 1] : '';
  const has = (n, l) => n.Labels && n.Labels.indexOf(l) >= 0;

  function shapeGraph(g, dbHint) {
    // Normalize field names: the C# parser serializes camelCase (id/labels/properties,
    // source/target) while the historic shape expected PascalCase (Id/Labels/Properties,
    // StartNodeId/EndNodeId). Both formats are supported.
    const normN = n => n.Id !== undefined ? n : { Id: n.id, Labels: n.labels, Properties: n.properties };
    const normR = r => r.Type !== undefined ? r : { Id: r.id, Type: r.type, StartNodeId: r.source, EndNodeId: r.target, Properties: r.properties };
    const N = (g.Nodes || g.nodes || []).map(normN);
    const R = (g.Relationships || g.relationships || []).map(normR);
    const byId = {}; for (const n of N) byId[n.Id] = n;

    // índices de relaciones por nodo origen / destino y por tipo
    const out = {}, inc = {};
    for (const r of R) {
      (out[r.StartNodeId] || (out[r.StartNodeId] = [])).push(r);
      (inc[r.EndNodeId] || (inc[r.EndNodeId] = [])).push(r);
    }
    const outOf = (id, t) => (out[id] || []).filter(r => r.Type === t);
    const incOf = (id, t) => (inc[id] || []).filter(r => r.Type === t);

    const database = dbHint || (N.find(n => has(n, 'SqlObject')) || { Properties: {} }).Properties.database || 'Base de datos';

    // stepId -> objId (dueño), via HAS_STEP
    const stepOwner = {};
    for (const r of R) if (r.Type === 'HAS_STEP') stepOwner[r.EndNodeId] = r.StartNodeId;
    const nameOf = id => byId[id] ? (byId[id].Properties.full_name || byId[id].Properties.name || id) : id;

    function buildFlowTree(stepNodes) {
      const root = [];
      for (const s of stepNodes) {
        const p = s.Properties; let level = root;
        const path = p.condition_path || [];
        const keys = p.condition_keys || [];   // "TYPE#line" por bloque: distingue ramas/bucles hermanos
        for (let ci = 0; ci < path.length; ci++) {
          const entry = path[ci];
          const key = keys[ci] || entry;       // fallback al texto si el grafo es antiguo (sin keys)
          const i = entry.indexOf(': ');
          const ctype = i >= 0 ? entry.slice(0, i) : entry;
          const ctext = i >= 0 ? entry.slice(i + 2) : '';
          let child = level.find(n => n.kind === 'cond' && n.key === key);
          if (!child) { child = { kind: 'cond', key, ctype, label: SD.naturalize(ctype, ctext), children: [] }; level.push(child); }
          level = child.children;
        }
        const sqlFrom = p.is_dynamic_sql ? outOf(s.Id, 'BUILDS_SQL_FROM').map(r => byId[r.EndNodeId].Properties.name) : [];
        // FILTERS_ON: columnas reales del WHERE/JOIN ON de este step (qué decidió
        // qué filas se tocaron) - distinto de los reads/writes ("qué se leyó/escribió").
        const filters = outOf(s.Id, 'FILTERS_ON').map(r => byId[r.EndNodeId].Properties);
        level.push({ kind: 'step', action: p.action, detail: p.detail || '', target: p.target_name || '', dynamic: !!p.is_dynamic_sql, dynSql: p.dynamic_sql || '', line: p.line_no, sqlFrom, filters });
      }
      return root;
    }

    // ── OBJETOS ───────────────────────────────────────────────
    const objects = N.filter(n => has(n, 'SqlObject')).map(n => {
      const id = n.Id, P = n.Properties;
      const steps = outOf(id, 'HAS_STEP').map(r => byId[r.EndNodeId]).sort((a, b) => (a.Properties.order || 0) - (b.Properties.order || 0));
      const stepIds = new Set(steps.map(s => s.Id));

      const callsOut = [...new Set(outOf(id, 'CALLS').map(r => nameOf(r.EndNodeId)))];
      const callsIn = [...new Set(incOf(id, 'CALLS').map(r => nameOf(r.StartNodeId)))];

      const reads = new Set(), writes = new Map(), writeTally = {};
      for (const s of steps) {
        for (const r of outOf(s.Id, 'READS_FROM')) reads.add(byId[r.EndNodeId].Properties.name);
        for (const r of outOf(s.Id, 'WRITES_TO')) {
          const t = byId[r.EndNodeId].Properties.name;
          writes.set(s.Properties.action + ' ' + t, { op: s.Properties.action, table: t });
          writeTally[t] = (writeTally[t] || 0) + 1;   // conteo bruto (no deduplicado)
        }
      }
      const writesByTable = Object.entries(writeTally).map(([table, count]) => ({ table, count }));

      const vars = outOf(id, 'DECLARES').map(r => byId[r.EndNodeId]).map(v => ({
        name: v.Properties.name, type: v.Properties.data_type,
        usedBy: incOf(v.Id, 'USES_VARIABLE').filter(r => stepIds.has(r.StartNodeId)).length,
        buildsSql: incOf(v.Id, 'BUILDS_SQL_FROM').length,
        assignedFrom: outOf(v.Id, 'ASSIGNED_FROM').map(r => { const c = byId[r.EndNodeId]; return `${c.Properties.table}(${c.Properties.name})`; }),
        construction: v.Properties.construction || [],   // RHS de cada asignación (arma SQL dinámico / valor de retorno)
      }));
      const params = outOf(id, 'HAS_PARAMETER').map(r => byId[r.EndNodeId]).map(p => ({ name: p.Properties.name, type: p.Properties.data_type, out: !!p.Properties.is_output }));

      // ── RUNTIME PLAN DATA ──────────────────────────────────────
      // When enrich-from-plans was run, READS_FROM/WRITES_TO relationships
      // carry actual_rows + confirmed_by (static edge verified by plan) or
      // source="execution_plan" (runtime-discovered, not visible statically).
      // Proc-level relationships (source=execution_plan) represent tables the
      // static analysis couldn't see (dynamic SQL resolved at runtime, views, etc.)
      const planStats = [];
      const fmtRows = n => n != null ? Number(n).toLocaleString() : null;
      // Step-level: confirmed static edges with actual row counts
      for (const s of steps) {
        for (const r of outOf(s.Id, 'WRITES_TO')) {
          const rp = r.Properties;
          if (rp.actual_rows != null || rp.confirmed_by === 'execution_plan')
            planStats.push({ table: rp.table, op: 'WRITE', rows: fmtRows(rp.actual_rows), discovered: false, op_label: s.Properties.action });
        }
        for (const r of outOf(s.Id, 'READS_FROM')) {
          const rp = r.Properties;
          if (rp.actual_rows != null || rp.confirmed_by === 'execution_plan')
            planStats.push({ table: rp.table, op: 'READ', rows: fmtRows(rp.actual_rows), discovered: false, op_label: 'READ' });
        }
      }
      // Proc-level: runtime-discovered edges (not in static analysis)
      for (const r of outOf(id, 'READS_FROM').concat(outOf(id, 'WRITES_TO'))) {
        const rp = r.Properties;
        if (rp.source === 'execution_plan')
          planStats.push({ table: rp.table, op: r.Type === 'WRITES_TO' ? 'WRITE' : 'READ', rows: fmtRows(rp.actual_rows), discovered: true, op_label: rp.action_type || r.Type });
      }
      const runtime = planStats.length > 0 ? {
        planSource: P.plan_source || '',
        rowsWritten: P.actual_rows_written != null ? Number(P.actual_rows_written).toLocaleString() : null,
        rowsRead: P.actual_rows_read != null ? Number(P.actual_rows_read).toLocaleString() : null,
        stats: planStats,
      } : null;

      return {
        kind: 'object', name: P.full_name,
        complexity: P.cyclomatic_complexity || 1, hasTx: !!P.has_transaction, hasErr: !!P.has_error_handling,
        hasCursor: !!P.has_cursor, dyn: P.dynamic_sql_calls || 0, parseError: P.parse_error || '',
        params, vars, callsOut, callsIn, reads: [...reads], writes: [...writes.values()], writesByTable,
        steps: steps.length, flow: buildFlowTree(steps), runtime,
      };
    });

    // ── TABLAS ────────────────────────────────────────────────
    const tables = N.filter(n => has(n, 'Table')).map(n => {
      const id = n.Id, P = n.Properties;
      const columns = outOf(id, 'HAS_COLUMN').map(r => byId[r.EndNodeId])
        .sort((a, b) => (a.Properties.ordinal || 99) - (b.Properties.ordinal || 99))
        .map(c => ({ name: c.Properties.name, type: c.Properties.data_type || '', pk: !!c.Properties.is_primary_key, nullable: c.Properties.is_nullable !== false, identity: !!c.Properties.is_identity }));

      const writers = [], readers = [], seenW = new Set(), seenR = new Set();
      const opCounts = {};
      for (const r of incOf(id, 'WRITES_TO')) {
        const o = nameOf(stepOwner[r.StartNodeId]); const op = byId[r.StartNodeId].Properties.action;
        opCounts[op] = (opCounts[op] || 0) + 1;
        const k = op + ' ' + o; if (!seenW.has(k)) { seenW.add(k); writers.push({ object: o, op }); }
      }
      for (const r of incOf(id, 'READS_FROM')) {
        opCounts['SELECT'] = (opCounts['SELECT'] || 0) + 1;
        const o = nameOf(stepOwner[r.StartNodeId]); if (!seenR.has(o)) { seenR.add(o); readers.push(o); }
      }

      const fkOut = outOf(id, 'FK_TO').map(r => ({ table: byId[r.EndNodeId].Properties.name, constraint: r.Properties.constraint || '' }));
      const fkIn = incOf(id, 'FK_TO').map(r => ({ table: byId[r.StartNodeId].Properties.name, constraint: r.Properties.constraint || '' }));

      const ops = Object.entries(opCounts).sort((a, b) => b[1] - a[1]);
      const totalCalls = ops.reduce((s, e) => s + e[1], 0);
      const relations = writers.length + readers.length + fkOut.length + fkIn.length;

      // Tabla temporal de SQL Server (#local / ##global): staging en tempdb, no es
      // esquema persistente. Se marca para distinguirla de las tablas reales.
      const temp = /^#/.test((P.name || '').split('.').pop());
      return { kind: 'table', name: P.name, temp, columns, writers, readers, fkOut, fkIn, ops, totalCalls, relations };
    });

    const byName = {};
    for (const o of objects) byName[o.name] = o;
    for (const t of tables) if (!byName[t.name]) byName[t.name] = t;

    const okObj = objects.filter(o => !o.parseError);
    const writeCounts = {};
    for (const o of okObj) for (const w of o.writes) writeCounts[w.table] = (writeCounts[w.table] || 0) + 1;

    const general = {
      totalObjects: objects.length, totalTables: tables.length,
      realTables: tables.filter(t => !t.temp).length, tempTables: tables.filter(t => t.temp).length,
      parseErrors: objects.length - okObj.length,
      withTx: okObj.filter(o => o.hasTx).length, withErr: okObj.filter(o => o.hasErr).length,
      withCursor: okObj.filter(o => o.hasCursor).length, withDyn: okObj.filter(o => o.dyn > 0).length,
      topComplexity: [...okObj].sort((a, b) => b.complexity - a.complexity).slice(0, 12).map(o => ({ name: o.name, cc: o.complexity, dyn: o.dyn })),
      hotspotWrites: Object.entries(writeCounts).sort((a, b) => b[1] - a[1]).slice(0, 12).map(([table, count]) => ({ table, count })),
    };

    const entities = objects.map(o => ({ name: o.name, kind: 'object', complexity: o.complexity, dyn: o.dyn, parseError: o.parseError }))
      .concat(tables.map(t => ({ name: t.name, kind: 'table', cols: t.columns.length, temp: t.temp })))
      .sort((a, b) => a.name.localeCompare(b.name));

    return { database, objects, tables, byName, entities, general };
  }

  SD.shape = function (raw, dbHint) {
    if (raw && (raw.Nodes || raw.nodes) && (raw.Relationships || raw.relationships)) return shapeGraph(raw, dbHint);
    throw new Error('Formato no reconocido: sube el graph_full.json (shape Neo4j con Nodes/Relationships).');
  };
})(window.SD = window.SD || {});
