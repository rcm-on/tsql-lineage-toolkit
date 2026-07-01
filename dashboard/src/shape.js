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
        // target_name es el texto crudo del AST (conserva corchetes: "[Schema].Proc")
        // y no coincide con byName si el SQL citó el identificador entre corchetes.
        // La arista TARGETS (Step -> SqlObject) ya resuelve el nodo real: usarla
        // cuando exista para que el enlace y la expansión recursiva de EXEC funcionen.
        const targetsEdge = outOf(s.Id, 'TARGETS')[0];
        const target = targetsEdge ? nameOf(targetsEdge.EndNodeId) : (p.target_name || '');
        level.push({ kind: 'step', action: p.action, detail: p.detail || '', target, dynamic: !!p.is_dynamic_sql, dynSql: p.dynamic_sql || '', line: p.line_no, sqlFrom, filters, selectStar: !!p.select_star });
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
        opKinds: v.Properties.op_kinds || [],            // operadores unidos de todas sus asignaciones (concat:+ = arma string)
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

      // ── TRIGGERS (creados dinámicamente por un proc) ──────────────
      // Nodo :SqlObject con label Trigger: no tiene cuerpo propio modelado (Fase A),
      // pero sí su tabla ON, evento/timing y quién lo CREATES. Ver
      // docs/dynamic-trigger-modeling-spec.md.
      const isTrigger = has(n, 'Trigger') || P.object_type === 'TRIGGER';
      const triggerOn = isTrigger
        ? [...new Set(outOf(id, 'ON').map(r => byId[r.EndNodeId] && byId[r.EndNodeId].Properties.name).filter(Boolean))]
        : [];
      const createdBy = isTrigger
        ? [...new Set(incOf(id, 'CREATES').map(r => nameOf(r.StartNodeId)))]
        : [];
      // Para un proc (no-trigger): los triggers que ESTE objeto crea (arista CREATES saliente).
      const createsTriggers = !isTrigger
        ? outOf(id, 'CREATES').map(r => nameOf(r.EndNodeId)).filter(Boolean)
        : [];

      return {
        kind: 'object', name: P.full_name,
        complexity: P.cyclomatic_complexity || 1, hasTx: !!P.has_transaction, hasErr: !!P.has_error_handling,
        hasCursor: !!P.has_cursor, dyn: P.dynamic_sql_calls || 0, parseError: P.parse_error || '',
        params, vars, callsOut, callsIn, reads: [...reads], writes: [...writes.values()], writesByTable,
        steps: steps.length, flow: buildFlowTree(steps), runtime,
        isTrigger, triggerOn, createdBy, createsTriggers,
        triggerEvents: P.trigger_events || [], triggerTiming: P.trigger_timing || '',
      };
    });

    // ── TABLAS ────────────────────────────────────────────────
    const tables = N.filter(n => has(n, 'Table')).map(n => {
      const id = n.Id, P = n.Properties;
      const columns = outOf(id, 'HAS_COLUMN').map(r => byId[r.EndNodeId])
        .sort((a, b) => (a.Properties.ordinal || 99) - (b.Properties.ordinal || 99))
        .map(c => ({
          name: c.Properties.name, type: c.Properties.data_type || '', pk: !!c.Properties.is_primary_key, nullable: c.Properties.is_nullable !== false, identity: !!c.Properties.is_identity,
          // DERIVES_FROM points TARGET column -> SOURCE column (see AstWalker/GraphExporter):
          // outgoing edges off this column are "what it's computed from".
          derivesFrom: outOf(c.Id, 'DERIVES_FROM').map(r2 => {
            const src = byId[r2.EndNodeId];
            // op_kinds: structured operators of the formula (arith:*, func:SUM, cast:...),
            // the queryable complement to the raw `logic` text - see OperatorClassifier.
            // via_computed_column marks a DDL computed column (CREATE TABLE ... AS (expr))
            // vs. a procedure's INSERT...SELECT lineage.
            return { table: src.Properties.table || '', column: src.Properties.name || '', logic: r2.Properties.logic || '', ops: r2.Properties.op_kinds || [], computed: !!r2.Properties.via_computed_column, line: r2.Properties.line_no, step: r2.Properties.caused_by_step || '' };
          }),
          // CONDITIONED_BY points WRITTEN column -> WHERE/JOIN-ON filter column
          // (business-rule lineage, not a calculation - no "logic" expression).
          conditionedBy: outOf(c.Id, 'CONDITIONED_BY').map(r2 => {
            const flt = byId[r2.EndNodeId];
            return { table: flt.Properties.table || '', column: flt.Properties.name || '', ops: r2.Properties.op_kinds || [], line: r2.Properties.line_no, step: r2.Properties.caused_by_step || '' };
          }),
        }))
        // A column is computed when any of its DERIVES_FROM edges is a DDL computed column.
        .map(c => ({ ...c, computed: c.derivesFrom.some(d => d.computed) }));

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
      // Triggers que se disparan cuando ESTA tabla cambia (arista ON entrante). Ángulo de
      // impacto: "si toco esta tabla, qué triggers saltan y ante qué evento".
      const triggers = incOf(id, 'ON').map(r => {
        const trg = byId[r.StartNodeId];
        return {
          name: nameOf(r.StartNodeId),
          events: (r.Properties && r.Properties.events) || (trg && trg.Properties.trigger_events) || [],
          timing: (r.Properties && r.Properties.timing) || (trg && trg.Properties.trigger_timing) || '',
        };
      });

      const totalCalls = ops.reduce((s, e) => s + e[1], 0);
      const relations = writers.length + readers.length + fkOut.length + fkIn.length + triggers.length;

      // Tabla temporal de SQL Server (#local / ##global): staging en tempdb, no es
      // esquema persistente. Se marca para distinguirla de las tablas reales.
      const temp = /^#/.test((P.name || '').split('.').pop());
      return { kind: 'table', name: P.name, temp, columns, writers, readers, fkOut, fkIn, ops, totalCalls, relations, triggers };
    });

    // ── Column-level DERIVES_FROM adjacency (for transitive depth/chain) ──────
    // DERIVES_FROM points TARGET column -> SOURCE column. We index both directions
    // off the raw edges so a walker (collineage.js) can answer, from any column:
    //   up   = provenance ("what is this ultimately computed from", target->sources)
    //   down = impact     ("what breaks if this changes",          source->targets)
    // Keyed by "table.column" (lowercased) so source/target columns resolve across
    // tables. Each edge carries logic + op_kinds so the chain shows *how* at each hop.
    const colAdj = { up: {}, down: {}, info: {} };
    const colKeyOf = n => `${(n.Properties.table || '').toLowerCase()}.${(n.Properties.name || '').toLowerCase()}`;
    for (const r of R) {
      if (r.Type !== 'DERIVES_FROM') continue;
      const tgt = byId[r.StartNodeId], src = byId[r.EndNodeId];
      if (!tgt || !src) continue;
      const tk = colKeyOf(tgt), sk = colKeyOf(src);
      colAdj.info[tk] = { table: tgt.Properties.table || '', column: tgt.Properties.name || '' };
      colAdj.info[sk] = { table: src.Properties.table || '', column: src.Properties.name || '' };
      const edge = { logic: r.Properties.logic || '', ops: r.Properties.op_kinds || [], computed: !!r.Properties.via_computed_column };
      (colAdj.up[tk] || (colAdj.up[tk] = [])).push({ key: sk, ...colAdj.info[sk], ...edge });
      (colAdj.down[sk] || (colAdj.down[sk] = [])).push({ key: tk, ...colAdj.info[tk], ...edge });
    }

    // ── Inventario de "derivados": columnas calculadas + variables ───────────
    // Un único sitio que recopila todo lo que se *calcula* (no se almacena tal
    // cual): columnas computed de DDL (con su fórmula, op_kinds y de qué dependen)
    // y variables de procedimientos (con su construcción + op_kinds, marcando las
    // que concatenan SQL dinámico). Material directo para el rule engine.
    const computedColumns = [];
    for (const t of tables)
      for (const c of t.columns)
        if (c.computed)
          computedColumns.push({
            table: t.name, column: c.name, type: c.type,
            logic: (c.derivesFrom[0] || {}).logic || '',
            ops: [...new Set([].concat(...c.derivesFrom.map(d => d.ops || [])))],
            sources: c.derivesFrom.map(d => ({ table: d.table, column: d.column })),
          });

    const variablesInv = [];
    for (const o of objects)
      for (const v of (o.vars || [])) {
        const ops = v.opKinds || [];
        const construction = v.construction || [];
        if (!ops.length && !construction.length) continue;   // declaración trivial: omitir
        variablesInv.push({
          object: o.name, name: v.name, type: v.type, ops, construction,
          dynamic: ops.includes('concat:+') || ops.includes('arith:+'),
        });
      }
    const derived = { computedColumns, variables: variablesInv };

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

    const entities = objects.map(o => ({ name: o.name, kind: 'object', complexity: o.complexity, dyn: o.dyn, parseError: o.parseError, isTrigger: o.isTrigger }))
      .concat(tables.map(t => ({ name: t.name, kind: 'table', cols: t.columns.length, temp: t.temp })))
      .sort((a, b) => a.name.localeCompare(b.name));

    return { database, objects, tables, byName, entities, general, colAdj, derived };
  }

  SD.shape = function (raw, dbHint) {
    if (raw && (raw.Nodes || raw.nodes) && (raw.Relationships || raw.relationships)) return shapeGraph(raw, dbHint);
    throw new Error('Formato no reconocido: sube el graph_full.json (shape Neo4j con Nodes/Relationships).');
  };
})(window.SD = window.SD || {});
