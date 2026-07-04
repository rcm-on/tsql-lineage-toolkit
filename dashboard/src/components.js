// Componentes de render (funciones que devuelven HTML + se cablean desde app.js).
// Cada uno es un "componente": Sidebar, Overview, ObjectView, FlowTree, Summary.
// Expuesto como SD.components.
(function (SD) {
  const esc = SD.charts.esc;

  // Banda de resumen automático (mini-resumen que no hay que pedir).
  function Summary(html) {
    return `<div class="summary"><span class="sumicon">✦</span><div>${html}</div></div>`;
  }

  // Métricas del flujo de control de un objeto: nº de decisiones (flujos de
  // control) y profundidad de anidación, calculadas del árbol.
  function flowMetrics(flow) {
    let conds = 0, depth = 0;
    (function walk(ns, d) {
      for (const n of (ns || [])) if (n.kind === 'cond') { conds++; depth = Math.max(depth, d + 1); walk(n.children, d + 1); }
    })(flow, 0);
    return { conds, depth };
  }

  const sevClass = { crit: 's-crit', high: 's-high', med: 's-med', low: 's-low', info: 's-info' };

  // Tabla de hallazgos (riesgos). showComponent: incluir columna de objeto/tabla.
  function FindingsTable(findings, showComponent) {
    if (!findings.length) return '<span class="muted">Sin hallazgos.</span>';
    const head = `<tr><th>Sev.</th><th>Categoría</th><th>Regla</th>${showComponent ? '<th>Componente</th>' : ''}<th>Detalle</th></tr>`;
    const rows = findings.map(f => `<tr class="${sevClass[f.sev]}">
      <td><span class="sev ${sevClass[f.sev]}">${SD.risks.SEVLABEL[f.sev]}</span></td>
      <td>${esc(f.cat)}</td>
      <td>${esc(f.rule)}</td>
      ${showComponent ? `<td><a href="#" onclick="SD.app.openObject('${esc(f.component)}');return false">${esc(f.component)}</a></td>` : ''}
      <td>${esc(f.detail)}</td></tr>`).join('');
    return `<table class="t risks">${head}${rows}</table>`;
  }

  function Sidebar(DATA, filter, kind) {
    const f = (filter || '').toLowerCase();
    const rows = DATA.entities.filter(e => e.name.toLowerCase().includes(f) && (!kind || kind === 'all' || e.kind === kind)).map(e => {
      const badges = e.kind === 'table'
        ? `<span class="b tbl">▦ ${e.cols}</span>`
        : [
            e.isTrigger ? `<span class="b trg">⚡TRG</span>` : '',
            e.complexity > 1 ? `<span class="b cc">cc${e.complexity}</span>` : '',
            e.dyn > 0 ? `<span class="b dyn">dyn${e.dyn}</span>` : '',
            e.parseError ? `<span class="b err">err</span>` : '',
          ].join('');
      return `<div class="item ${e.kind}" data-n="${esc(e.name)}"><span class="nm">${esc(e.name)}</span><span class="badges">${badges}</span></div>`;
    }).join('');
    return rows || `<div class="muted" style="padding:12px">Sin coincidencias</div>`;
  }

  function Overview(DATA) {
    const g = DATA.general;
    const risk = SD.risks.analyze(DATA);
    const donut = SD.charts.donut([
      { label: 'SQL dinámico', value: g.withDyn },
      { label: 'Transacción', value: g.withTx },
      { label: 'Errores (TRY/CATCH)', value: g.withErr },
      { label: 'Cursor', value: g.withCursor },
    ]);
    const topBars = SD.charts.bars(g.topComplexity.map(x => ({
      label: x.name + (x.dyn > 0 ? ` (dyn ${x.dyn})` : ''), value: x.cc,
      onClick: `SD.app.openObject('${esc(x.name)}')`,
    })));
    const hotBars = SD.charts.bars(g.hotspotWrites.map(x => ({ label: x.table, value: x.count, color: '#ff8a80' })));
    return `
      ${Summary(SD.summary.db(DATA))}
      <div class="cards">
        <div class="card clk" onclick="SD.app.setFilter('object')"><div class="n">${g.totalObjects}</div><div class="l">Objetos ›</div></div>
        <div class="card clk" onclick="SD.app.setFilter('table')"><div class="n">${g.realTables != null ? g.realTables : g.totalTables}</div><div class="l">Tablas ›</div></div>
        ${g.tempTables ? `<div class="card clk" onclick="SD.app.setFilter('temp')"><div class="n">${g.tempTables}</div><div class="l">Temporales (#) ›</div></div>` : ''}
        <div class="card clk" onclick="SD.app.setFilter('object','dyn')"><div class="n">${g.withDyn}</div><div class="l">SQL dinámico ›</div></div>
        <div class="card"><div class="n">${g.withTx}</div><div class="l">Transacción</div></div>
        <div class="card"><div class="n">${g.withErr}</div><div class="l">Manejo errores</div></div>
        <div class="card"><div class="n">${g.withCursor}</div><div class="l">Cursor</div></div>
        <div class="card clk risk-card" onclick="SD.app.openRisks()"><div class="n">${risk.counts.crit + risk.counts.high || risk.total}</div><div class="l">⚠ Riesgos ›</div></div>
      </div>
      <div class="grid2">
        <div><h3>Características</h3>${donut}</div>
        <div><h3>Top complejidad ciclomática</h3>${topBars}</div>
      </div>
      <h3>Tablas más escritas</h3>${hotBars}
    `;
  }

  // Categoría visual de la acción del paso (para colorear: lectura / escritura / llamada / control).
  function actClass(a) {
    if (a === 'SELECT') return 'a-read';
    if (['INSERT', 'UPDATE', 'DELETE', 'MERGE', 'ALTER'].includes(a)) return 'a-write';
    if (a === 'EXEC') return 'a-call';
    return 'a-ctrl'; // RETURN, THROW, BEGIN_TRAN, COMMIT_TRAN, ROLLBACK…
  }

  // Agrupa las columnas de FILTERS_ON por tabla para mostrarlas legibles:
  // [{table:'A',name:'x'},{table:'A',name:'y'},{table:'B',name:'z'}] -> ["A(x, y)", "B(z)"].
  function groupFilters(filters) {
    if (!filters || !filters.length) return [];
    const byTable = new Map();
    for (const f of filters) {
      const cols = byTable.get(f.table) || [];
      if (!cols.includes(f.name)) cols.push(f.name);
      byTable.set(f.table, cols);
    }
    return [...byTable.entries()].map(([table, cols]) => `${table}(${cols.join(', ')})`);
  }

  // depth/maxDepth/stack: expande recursivamente los pasos EXEC con el flow del
  // procedimiento llamado (hasta maxDepth niveles), igual que la cadena de impacto.
  // stack lleva la pila de llamadas activa para detectar recursión y no colgarse.
  function FlowTree(nodes, byName, depth, maxDepth, stack) {
    depth = depth || 0;
    maxDepth = maxDepth == null ? 3 : maxDepth;
    stack = stack || [];
    return nodes.map(n => {
      if (n.kind === 'cond')
        return `<details open><summary class="cond">${esc(n.label)}</summary>${FlowTree(n.children, byName, depth, maxDepth, stack)}</details>`;
      const tgt = n.target ? ` <span class="tgt">→ ${esc(n.target)}</span>` : '';
      const from = (n.sqlFrom && n.sqlFrom.length) ? ` ← ${esc(n.sqlFrom.join(', '))}` : '';
      const dyn = n.dynamic ? ` <span class="dyn">[SQL dinámico${from}]</span>` : '';
      const runs = n.dynSql ? ` <span class="dyn">⚡ ejecuta: ${esc(n.dynSql)}</span>` : '';
      const filterGroups = groupFilters(n.filters);
      const filt = filterGroups.length ? ` <span class="filt">⌕ ${esc(filterGroups.join(', '))}</span>` : '';
      const link = byName[n.target] ? ` <a href="#" onclick="SD.app.openObject('${esc(n.target)}');return false" class="muted" title="ir">↗</a>` : '';
      const detail = n.detail ? ` <span class="muted">(${esc(n.detail)})</span>` : '';
      const line = `<div class="step"><span class="act ${actClass(n.action)}">${esc(n.action)}</span>${detail}${tgt}${dyn}${runs}${filt}${link} <span class="muted">· L${n.line}</span></div>`;
      let nested = '';
      if (n.action === 'EXEC' && n.target && byName[n.target]) {
        const calleeFlow = byName[n.target].flow;
        if (stack.includes(n.target)) {
          nested = `<div class="step muted">↻ recursión: ${esc(n.target)} ya está en la pila de llamadas, no se expande</div>`;
        } else if (depth >= maxDepth) {
          nested = calleeFlow && calleeFlow.length
            ? `<div class="step muted">… nivel máximo (${maxDepth}) alcanzado, ver ${esc(n.target)} <a href="#" onclick="SD.app.openObject('${esc(n.target)}');return false">↗</a></div>`
            : '';
        } else if (calleeFlow && calleeFlow.length) {
          nested = `<details class="tree-fold sub"><summary>↳ pasos de ${esc(n.target)}</summary>${FlowTree(calleeFlow, byName, depth + 1, maxDepth, stack.concat(n.target))}</details>`;
        }
      }
      return line + nested;
    }).join('');
  }

  // Mermaid: limpia caracteres que rompen el parser dentro de `"label"` (comillas,
  // pipes, brackets/llaves/ángulos, saltos de línea).
  function mmSanitize(s) {
    return (s || '').replace(/[\r\n]+/g, ' ').replace(/["|{}\[\]<>]/g, '').trim();
  }
  function mmEsc(s) { return (s || '').replace(/"/g, "'"); }
  // Envuelve el texto en varias líneas (<br>) para que las cajas no crezcan a
  // lo ancho y el texto siga legible. Sanitiza primero; el <br> se añade después
  // (mmSanitize quita `<>`) y Mermaid lo respeta con htmlLabels (securityLevel loose).
  function mmWrap(s, width) {
    const words = mmSanitize(s).split(' ');
    const lines = []; let cur = '';
    for (const w of words) {
      if (cur && (cur.length + 1 + w.length) > width) { lines.push(cur); cur = w; }
      else cur = cur ? cur + ' ' + w : w;
    }
    if (cur) lines.push(cur);
    return lines.join('<br>');
  }

  // Clase de estilo Mermaid por tipo de acción (colores via classDef, ver MM_DEFS).
  const MM_CLASS = {
    SELECT: 'rd', INSERT: 'wr', UPDATE: 'wr', DELETE: 'wr', MERGE: 'wr',
    EXEC: 'cl',
    CREATE_TABLE: 'ddl', CREATE_INDEX: 'ddl', ALTER: 'ddl', TRUNCATE: 'ddl', DROP_TABLE: 'ddl',
    DECLARE_CURSOR: 'cur', OPEN_CURSOR: 'cur', FETCH: 'cur', CLOSE_CURSOR: 'cur', DEALLOCATE: 'cur',
  };
  const mmClass = a => MM_CLASS[a] || 'ct';
  const MM_DEFS = [
    'classDef rd fill:#1e3a5a,stroke:#9cdcfe,color:#9cdcfe',
    'classDef wr fill:#5a2a2a,stroke:#ff8a80,color:#ff8a80',
    'classDef cl fill:#3a2a5a,stroke:#c8a2e8,color:#c8a2e8',
    'classDef ddl fill:#3a3414,stroke:#d4c44a,color:#d4c44a',
    'classDef cur fill:#1e3a3a,stroke:#80cbc4,color:#80cbc4',
    'classDef ct fill:#333333,stroke:#bbbbbb,color:#bbbbbb',
    'classDef cond fill:#2a2718,stroke:#ff9800,color:#ff9800',
    'classDef term fill:#37373d,stroke:#e91e8c,color:#ffffff',
  ];

  // Flujograma de control en Mermaid (`flowchart TD`) a partir del árbol `flow`
  // (cond/step anidados de buildFlowTree). WHILE = bucle real (vuelve al rombo);
  // IF/IF_ELSE/CATCH = rama que reconverge tras el rombo. Steps cuyo `target`
  // esté en `byName` quedan navegables (click → sdOpen).
  function FlowChartMermaid(flow, byName) {
    if (!flow || !flow.length) return null;
    const lines = ['flowchart TD'];
    const clicks = [];
    let counter = 0;
    const nid = () => 'n' + (++counter);
    const connect = (pending, target) => {
      for (const p of pending) lines.push(`${p.id} -->${p.label ? `|${mmSanitize(p.label)}|` : ''} ${target}`);
    };

    // `incoming`: edges pendientes hacia el primer nodo de esta secuencia.
    // Devuelve los edges pendientes hacia lo que venga después.
    function walk(nodes, incoming) {
      let pending = incoming;
      for (const node of nodes) {
        if (node.kind === 'step') {
          const id = nid();
          // SQL dinámico: si el parser reconstruyó el literal ejecutado (dynSql),
          // mostramos QUÉ ejecuta (`EXEC ⚡ CREATE SERVER AUDIT …`); si no (se arma en
          // runtime), mostramos la(s) variable(s) que lo alimentan (`EXEC ⚡ @SQL`). El
          // target suele ser el placeholder "(dynamic SQL)" inútil, se omite. · Ln
          // distingue EXEC repetidos y localiza el origen.
          const run = node.dynSql ? ` ⚡ ${node.dynSql.length > 90 ? node.dynSql.slice(0, 90) + '…' : node.dynSql}` : '';
          const dyn = node.dynamic && !node.dynSql
            ? (node.sqlFrom && node.sqlFrom.length ? ` ⚡ ${node.sqlFrom.join(', ')}` : ' ⚡din')
            : '';
          const tgt = (node.target && node.target !== '(dynamic SQL)') ? ` → ${node.target}` : '';
          const filterGroups = groupFilters(node.filters);
          const filt = filterGroups.length ? ` ⌕ ${filterGroups.join(', ')}` : '';
          const txt = node.action + (node.detail ? ` (${node.detail})` : '') + tgt + run + dyn + filt + (node.line ? ` · L${node.line}` : '');
          lines.push(`${id}["${mmWrap(txt, 34)}"]:::${mmClass(node.action)}`);
          connect(pending, id);
          if (node.target && byName[node.target]) clicks.push(`click ${id} call sdOpen("${mmEsc(node.target)}")`);
          pending = [{ id }];
        } else {
          const id = nid();
          // Rect redondeado (no rombo): el diamante reventaba con condiciones largas
          // (hasta 90 chars). Texto envuelto + clase `cond` (naranja) para que se
          // siga leyendo como decisión. El texto íntegro queda en el árbol de texto.
          lines.push(`${id}("${mmWrap(node.label, 30)}"):::cond`);
          connect(pending, id);
          if (node.ctype === 'WHILE') {
            const bodyOut = walk(node.children, [{ id, label: 'repetir' }]);
            connect(bodyOut, id);                 // vuelve al rombo: bucle real
            pending = [{ id, label: 'no' }];
          } else {                                 // IF / IF_ELSE / CATCH: rama reconverge
            const branchOut = walk(node.children, [{ id, label: 'sí' }]);
            pending = [{ id, label: 'no' }, ...branchOut];
          }
        }
      }
      return pending;
    }

    lines.push('S0(["INICIO"]):::term');
    const out = walk(flow, [{ id: 'S0' }]);
    lines.push('E0(["FIN"]):::term');
    connect(out, 'E0');
    return [...lines, ...MM_DEFS, ...clicks].join('\n');
  }

  // Flujo de datos "super-detallado" (`flowchart LR`): entradas (parámetros IN +
  // tablas leídas) → objeto central → salidas (escrituras, EXEC, parámetros OUTPUT).
  // Tablas/SPs navegables si están en DATA.byName.
  function DataFlowMermaid(o, DATA) {
    const inputs = [];
    for (const p of o.params.filter(p => !p.out)) inputs.push({ label: `${p.name}: ${p.type}` });
    const outputs = [];
    // Acción siempre Objeto -> Tabla (igual que una escritura): el objeto "lee"
    // la tabla, no al revés - por eso va en "Salidas" junto a INSERT/UPDATE/...,
    // no en "Entradas" (que queda solo para parámetros, el único caso donde el
    // dato realmente fluye HACIA el objeto sin que el objeto actúe sobre nada).
    for (const t of o.reads) outputs.push({ label: `lee ${t}`, link: t, op: 'lee' });
    for (const w of o.writes) outputs.push({ label: `${w.op} ${w.table}`, link: w.table, op: w.op });
    for (const c of o.callsOut) outputs.push({ label: `EXEC ${c}`, link: c, op: 'EXEC' });
    for (const p of o.params.filter(p => p.out)) outputs.push({ label: `${p.name} (OUTPUT)` });
    if (!inputs.length && !outputs.length) return null;

    const lines = ['flowchart LR'];
    const clicks = [];
    let counter = 0;
    const nid = () => 'd' + (++counter);

    lines.push('subgraph IN["Entradas"]');
    for (const i of inputs) { i.id = nid(); lines.push(`${i.id}["${mmSanitize(i.label)}"]`); }
    lines.push('end');
    lines.push(`OBJ{{"${mmSanitize(o.name)}"}}`);
    lines.push('subgraph OUT["Salidas"]');
    for (const out of outputs) { out.id = nid(); lines.push(`${out.id}["${mmSanitize(out.label)}"]`); }
    lines.push('end');

    for (const i of inputs) {
      lines.push(`${i.id} --> OBJ`);
      if (i.link && DATA.byName[i.link]) clicks.push(`click ${i.id} call sdOpen("${mmEsc(i.link)}")`);
    }
    for (const out of outputs) {
      lines.push(`OBJ -->${out.op ? `|${mmSanitize(out.op)}|` : ''} ${out.id}`);
      if (out.link && DATA.byName[out.link]) clicks.push(`click ${out.id} call sdOpen("${mmEsc(out.link)}")`);
    }
    return [...lines, ...clicks].join('\n');
  }

  // Cadena de impacto (`flowchart LR`) hasta N niveles, en ambas direcciones,
  // combinando CALLS (entre objetos) y reads/writes (objeto<->tabla) - ver
  // SD.impact.chain (impact.js) para el BFS. Un `subgraph` por nivel (negativo
  // = upstream/"qué alimenta esto", positivo = downstream/"qué afecta esto")
  // para que la profundidad se vea como columnas, no como un grafo plano.
  const IMPACT_CLASSDEFS = [
    'classDef obj fill:#2a2a30,stroke:#9cdcfe,color:#9cdcfe',
    'classDef tbl fill:#2a3a2a,stroke:#9be7b4,color:#9be7b4',
    'classDef root fill:#3a2a5a,stroke:#c8a2e8,color:#ffffff,stroke-width:2px',
    'classDef more fill:#2a2a2a,stroke:#777,color:#999',
  ];
  function ImpactChainMermaid(rootName, DATA, maxDepth) {
    const { levels, edges } = SD.impact.chain(rootName, DATA, maxDepth);
    if (!levels.length) return null;

    const ids = {};        // name -> mermaid node id
    let counter = 0;
    const idOf = name => ids[name] || (ids[name] = 'i' + (++counter));

    const lines = ['flowchart LR'];
    const clicks = [];
    const rootLevelIdx = levels.findIndex(lv => lv.some(n => n.name === rootName));

    levels.forEach((levelNodes, li) => {
      const depth = li - rootLevelIdx;
      if (depth === 0) {
        for (const n of levelNodes) {
          const id = idOf(n.name);
          lines.push(`${id}{{"${mmWrap(n.name, 28)}"}}:::root`);
          clicks.push(`click ${id} call sdOpen("${mmEsc(n.name)}")`);
        }
        return;
      }
      const label = depth < 0 ? `Nivel ${depth}` : `Nivel +${depth}`;
      lines.push(`subgraph L${li}["${label}"]`);
      for (const n of levelNodes) {
        const id = idOf(n.name);
        if (n.kind === 'more') { lines.push(`${id}["${mmSanitize(n.name)}"]:::more`); continue; }
        const shape = n.kind === 'table' ? `[["${mmWrap(n.name, 24)}"]]` : `["${mmWrap(n.name, 24)}"]`;
        lines.push(`${id}${shape}:::${n.kind === 'table' ? 'tbl' : 'obj'}`);
        if (DATA.byName[n.name]) clicks.push(`click ${id} call sdOpen("${mmEsc(n.name)}")`);
      }
      lines.push('end');
    });

    for (const e of edges) {
      const a = ids[e.from], b = ids[e.to];
      if (!a || !b) continue;
      lines.push(`${a} -->${e.label ? `|${mmSanitize(e.label)}|` : ''} ${b}`);
    }

    return [...lines, ...IMPACT_CLASSDEFS, ...clicks].join('\n');
  }

  function paramTable(title, ps) {
    if (!ps.length) return '';
    return `<h3>${title} (${ps.length})</h3><table class="t"><tr><th>Nombre</th><th>Tipo</th></tr>` +
      ps.map(p => `<tr><td>${esc(p.name)}</td><td>${esc(p.type)}</td></tr>`).join('') + `</table>`;
  }

  // Selector de profundidad (1-5) para la cadena de impacto, compartido por
  // ObjectView/TableView. Re-renderiza toda la vista al cambiar (SD.app.setImpactDepth).
  function depthSelector(depth) {
    const opts = [1, 2, 3, 4, 5].map(n => `<option value="${n}" ${n === depth ? 'selected' : ''}>${n} nivel${n > 1 ? 'es' : ''}</option>`).join('');
    return `<select onchange="SD.app.setImpactDepth(this.value)">${opts}</select>`;
  }

  function impactSection(name, DATA, depth) {
    const def = ImpactChainMermaid(name, DATA, depth);
    return `<h3>Cadena de impacto ${depthSelector(depth)}</h3>${def ? SD.mm.block(def, 'Cadena de impacto') : '<span class="muted">sin relaciones encadenables</span>'}`;
  }

  function ObjectView(o, DATA, impactDepth) {
    const chip = n => DATA.byName[n] ? `<span class="chip" onclick="SD.app.openObject('${esc(n)}')">${esc(n)}</span>` : `<span class="chip muted">${esc(n)}</span>`;
    const actionTally = () => {
      const counts = {};
      (function walk(ns) { for (const n of ns) { if (n.kind === 'step') counts[n.action] = (counts[n.action] || 0) + 1; else walk(n.children); } })(o.flow);
      const pairs = Object.entries(counts).sort((a, b) => b[1] - a[1]);
      return pairs.length ? SD.charts.tally(pairs) : '<span class="muted">sin pasos</span>';
    };
    const fmtCtor = c => (c && c.length) ? `<code class="ctor">${c.map(esc).join('<br>+ ')}</code>` : '<span class="muted">-</span>';
    const varRow = v => `<tr><td>${esc(v.name)}</td><td>${esc(v.type)}</td><td>${v.usedBy}</td>` +
      `<td>${v.buildsSql > 0 ? `<span class="risk">⚠ ${v.buildsSql}</span>` : '-'}</td>` +
      `<td>${v.assignedFrom.length ? esc(v.assignedFrom.join('; ')) : '-'}</td>` +
      `<td>${fmtCtor(v.construction)}</td></tr>`;
    // SQL dinámico: las variables que lo arman, con su construcción textual.
    const dynVars = o.vars.filter(v => v.buildsSql > 0 && v.construction.length);
    const fm = flowMetrics(o.flow);
    const findings = SD.risks.forComponent(DATA, o.name);
    // Trigger creado dinámicamente: no tiene cuerpo/metricas propios (Fase A), pero sí
    // su tabla ON, evento/timing y el/los proc(s) que lo CREATES.
    const triggerBlock = o.isTrigger ? `
      <div class="flags">⚡ TRIGGER${o.triggerTiming ? ` · ${esc(o.triggerTiming)}` : ''}${o.triggerEvents && o.triggerEvents.length ? ` ${esc(o.triggerEvents.join(', '))}` : ''}</div>
      <h3>Trigger</h3>
      <table class="grid">
        <tr><td>Se dispara sobre (ON)</td><td>${o.triggerOn && o.triggerOn.length ? o.triggerOn.map(t => `<b>${esc(t)}</b>`).join(', ') : '—'}</td></tr>
        <tr><td>Eventos</td><td>${o.triggerEvents && o.triggerEvents.length ? esc(o.triggerEvents.join(', ')) : '—'}</td></tr>
        <tr><td>Timing</td><td>${esc(o.triggerTiming || '—')}</td></tr>
        <tr><td>Creado por</td><td>${o.createdBy && o.createdBy.length ? o.createdBy.map(c => `<b>${esc(c)}</b>`).join(', ') : '—'}</td></tr>
      </table>` : '';
    return `
      <h2>${esc(o.name)}${o.isTrigger ? ' <span class="b trg">⚡TRG</span>' : ''}</h2>
      <div class="flags">tx=${o.hasTx} · errores=${o.hasErr} · cursor=${o.hasCursor}</div>
      ${triggerBlock}
      ${Summary(SD.summary.object(o))}

      <h3>Métricas</h3>
      <div class="cards">
        <div class="card"><div class="n">${o.complexity}</div><div class="l">Complejidad ciclomática</div></div>
        <div class="card"><div class="n">${fm.conds}</div><div class="l">Flujos de control</div></div>
        <div class="card"><div class="n">${fm.depth}</div><div class="l">Profundidad anidación</div></div>
        <div class="card"><div class="n">${o.steps}</div><div class="l">Pasos</div></div>
        <div class="card"><div class="n">${o.dyn}</div><div class="l">SQL dinámico</div></div>
        <div class="card"><div class="n">${o.vars.length}</div><div class="l">Variables</div></div>
      </div>

      ${findings.length ? `<h3>Riesgos del objeto (${findings.length})</h3>${FindingsTable(findings, false)}` : ''}

      <div class="grid2">
        <div>
          <h3>Referencias</h3>
          <div><b>A quién llama:</b> ${o.callsOut.length ? o.callsOut.map(chip).join('') : '<span class="muted">nadie</span>'}</div>
          <div style="margin-top:6px"><b>Quién le llama:</b> ${o.callsIn.length ? o.callsIn.map(chip).join('') : '<span class="muted">nadie</span>'}</div>
          <div style="margin-top:6px"><b>Tablas:</b><br>${o.reads.map(t => `<span class="chip r">leída · ${esc(t)}</span>`).join('')} ${o.writes.map(w => `<span class="chip w">${esc(w.op)} · ${esc(w.table)}</span>`).join('') || '<span class="muted">ninguna</span>'}</div>
          ${o.createsTriggers && o.createsTriggers.length ? `<div style="margin-top:6px"><b>⚡ Crea triggers:</b> ${o.createsTriggers.map(chip).join('')}</div>` : ''}
        </div>
        <div><h3>Acciones por tipo</h3>${actionTally()}</div>
      </div>

      ${dynVars.length ? `<h3>Construcción del SQL dinámico ⚠</h3>${dynVars.map(v => `<div class="ctor-block"><b>${esc(v.name)}</b> se arma como:<br><code class="ctor">${v.construction.map(esc).join('<br>+ ')}</code></div>`).join('')}` : ''}

      ${paramTable('Variables de entrada (parámetros)', o.params.filter(p => !p.out))}
      ${paramTable('Variables de salida / retorno (OUTPUT)', o.params.filter(p => p.out))}
      ${o.vars.length ? `<h3>Variables temporales (locales)</h3><table class="t"><tr><th>Nombre</th><th>Tipo</th><th>Usada por</th><th>SQL din.</th><th>Se llena de</th><th>Construcción / valor</th></tr>${o.vars.map(varRow).join('')}</table>` : ''}

      <h3>Grafo rápido (llamadas)</h3>
      ${SD.charts.miniGraph(o.name,
        o.callsIn.map(n => ({ label: n, dir: 'in', role: 'le llama', onClick: DATA.byName[n] ? `SD.app.openObject('${esc(n)}')` : '' }))
          .concat(o.callsOut.map(n => ({ label: n, dir: 'out', role: 'llama a', onClick: DATA.byName[n] ? `SD.app.openObject('${esc(n)}')` : '' }))))}

      <h3>Flujograma de control (desde INICIO hasta FIN)</h3>
      ${o.flow.length ? SD.mm.block(FlowChartMermaid(o.flow, DATA.byName), 'Flujograma de control') : '<span class="muted">sin pasos</span>'}
      ${o.flow.length ? `<details class="tree-fold"><summary>Ver como árbol de texto (condiciones en lenguaje natural, EXEC expandido hasta ${impactDepth || 3} niveles)</summary>
      <div class="tree">${FlowTree(o.flow, DATA.byName, 0, impactDepth || 3, [o.name])}</div></details>` : ''}

      ${(() => { const df = DataFlowMermaid(o, DATA); return df ? `<h3>Flujo de datos</h3>${SD.mm.block(df, 'Flujo de datos')}` : ''; })()}

      ${impactSection(o.name, DATA, impactDepth || 3)}

      ${o.runtime ? (() => {
        const rt = o.runtime;
        const totals = [
          rt.rowsWritten != null ? `<span class="chip w">escritas: <b>${esc(rt.rowsWritten)}</b></span>` : '',
          rt.rowsRead    != null ? `<span class="chip r">leídas: <b>${esc(rt.rowsRead)}</b></span>` : '',
        ].filter(Boolean).join(' ');
        const rows = rt.stats.map(s => {
          const disc = s.discovered
            ? `<span class="b dyn" title="No visible en análisis estático; descubierto al ejecutar">⚡ descubierto</span>`
            : `<span class="b" style="background:var(--ok,#2a4a2a);color:#7ef07e" title="Confirmado por plan de ejecución">✓ confirmado</span>`;
          const rowsTd = s.rows != null ? `<b>${esc(s.rows)}</b>` : '<span class="muted">—</span>';
          const opCls = s.op === 'WRITE' ? 'a-write' : 'a-read';
          return `<tr>
            <td>${esc(s.table)}</td>
            <td><span class="act ${opCls}">${esc(s.op_label || s.op)}</span></td>
            <td style="text-align:right">${rowsTd}</td>
            <td>${disc}</td>
          </tr>`;
        }).join('');
        return `
          <h3>📊 Ejecución real (plan de ejecución SQL Server)</h3>
          <div class="summary" style="border-left-color:#4db6ac">
            <span class="sumicon" style="color:#4db6ac">▶</span>
            <div>Datos capturados del plan de ejecución real.
            ${rt.planSource ? `Plan: <code>${esc(rt.planSource)}</code>.` : ''}
            ${totals || ''}
            <span class="muted" style="display:block;margin-top:4px;font-size:11px">
              ✓ confirmado = la tabla ya estaba en el análisis estático y el plan lo verifica.
              ⚡ descubierto = tabla solo visible en runtime (SQL dinámico resuelto, vistas, etc.).
            </span>
            </div>
          </div>
          <table class="t">
            <tr><th>Tabla</th><th>Operación</th><th style="text-align:right">Filas reales</th><th>Estado</th></tr>
            ${rows}
          </table>`;
      })() : ''}
    `;
  }

  // Linaje transitivo de columnas (profundidad): para cada columna de la tabla con
  // cadena DERIVES_FROM, muestra en ambos sentidos hasta qué columnas raíz/consumidoras
  // llega y a cuántos saltos (hops), con la cadena ordenada y el op_kind de cada paso.
  // Es la versión navegable de @col_provenance / @col_impact - ver collineage.js.
  function columnLineageSection(t, DATA) {
    if (!DATA.colAdj) return '';
    const opk = ops => !ops || !ops.length ? '' :
      ` ${ops.map(o => `<span class="opk opk-${esc(o.split(':')[0])}" title="${esc(o)}">${esc(o.split(':')[1] || o)}</span>`).join('')}`;
    const arrow = (dir, e) => {
      const sep = dir === 'up' ? ' ← ' : ' → ';
      const path = e.chain.map(c => `${c.table}.${c.column}`).join(sep);
      const link = DATA.byName[e.table] ? `onclick="SD.app.openObject('${esc(e.table)}')"` : '';
      return `<span class="chip" title="${esc(path)}" ${link}>${esc(e.table)}.${esc(e.column)}<span class="hops">·n${e.hops}</span></span>${opk(e.ops)}`;
    };
    const rows = [];
    for (const c of t.columns) {
      const prov = SD.collineage.provenance(t.name, c.name, DATA, 20);
      const imp = SD.collineage.impact(t.name, c.name, DATA, 20);
      if (!prov.length && !imp.length) continue;
      rows.push(`<tr>
        <td>${esc(c.name)}</td>
        <td>${prov.length ? prov.slice(0, 30).map(e => arrow('up', e)).join('') : '<span class="muted">—</span>'}</td>
        <td>${imp.length ? imp.slice(0, 30).map(e => arrow('down', e)).join('') : '<span class="muted">—</span>'}</td>
      </tr>`);
    }
    if (!rows.length) return '';
    return `<h3>Linaje transitivo de columnas (profundidad)</h3>
      <p class="muted" style="margin:2px 0 8px">Por columna: de qué columnas deriva (provenance) y qué columnas se ven afectadas si cambia (impacto), con el nº de saltos <code>·nN</code> y el operador de cada paso. Pasa el ratón para ver la cadena completa.</p>
      <table class="t"><tr><th>Columna</th><th>Deriva de (↑ provenance)</th><th>Impacta a (↓ impacto)</th></tr>${rows.join('')}</table>`;
  }

  function TableView(t, DATA, impactDepth) {
    const chip = n => DATA.byName[n] ? `<span class="chip" onclick="SD.app.openObject('${esc(n)}')">${esc(n)}</span>` : `<span class="chip muted">${esc(n)}</span>`;
    // op_kinds badge: the structured operators behind the dependency (arith:*, func:SUM,
    // logical:AND, cast:...). The category (text before ':') drives a color so the kind of
    // transformation reads at a glance - arithmetic/cast = type-change risk, logical = row
    // selection. Empty for a plain column copy.
    const opBadges = ops => !ops || !ops.length ? '' :
      ` ${ops.map(o => `<span class="opk opk-${esc(o.split(':')[0])}" title="${esc(o)}">${esc(o.split(':')[1] || o)}</span>`).join('')}`;
    const deriv = d => `<span class="chip" title="${esc(d.logic)}${d.line != null ? ' (línea ' + d.line + ')' : ''}" ${DATA.byName[d.table] ? `onclick="SD.app.openObject('${esc(d.table)}')"` : ''}>${esc(d.table)}.${esc(d.column)}</span>${opBadges(d.ops)}`;
    const cond = d => `<span class="chip" title="WHERE/JOIN${d.line != null ? ' (línea ' + d.line + ')' : ''}" ${DATA.byName[d.table] ? `onclick="SD.app.openObject('${esc(d.table)}')"` : ''}>${esc(d.table)}.${esc(d.column)}</span>${opBadges(d.ops)}`;
    const colRow = c => `<tr><td>${c.pk ? '🔑 ' : ''}${esc(c.name)}${c.computed ? ' <span class="tag-calc" title="Columna calculada (CREATE TABLE ... AS (expresión))">🧮 calculada</span>' : ''}</td><td>${esc(c.type)}</td><td>${c.nullable ? '' : 'NOT NULL'}</td><td>${c.identity ? 'IDENTITY' : ''}</td><td>${c.derivesFrom && c.derivesFrom.length ? c.derivesFrom.map(deriv).join('') : ''}</td><td>${c.conditionedBy && c.conditionedBy.length ? c.conditionedBy.map(cond).join('') : ''}</td></tr>`;
    const neighbors = t.writers.map(w => ({ label: w.object, dir: 'in', color: '#ff8a80', role: w.op, onClick: DATA.byName[w.object] ? `SD.app.openObject('${esc(w.object)}')` : '' }))
      .concat(t.readers.map(r => ({ label: r, dir: 'in', color: '#9cdcfe', role: 'lee', onClick: DATA.byName[r] ? `SD.app.openObject('${esc(r)}')` : '' })))
      .concat(t.fkOut.map(f => ({ label: f.table, dir: 'out', color: '#cddc39', role: 'FK→', onClick: DATA.byName[f.table] ? `SD.app.openObject('${esc(f.table)}')` : '' })));
    return `
      <h2>${t.temp ? '⌗' : '▦'} ${esc(t.name)} <span class="muted" style="font-size:13px">(${t.temp ? 'tabla temporal · tempdb' : 'tabla'})</span></h2>
      ${t.temp ? `<div class="summary" style="border-left-color:var(--warn)"><span class="sumicon" style="color:var(--warn)">⌗</span><div>Tabla <b>temporal</b> de SQL Server: existe solo durante la ejecución del objeto que la crea (staging en <code>tempdb</code>), no es parte del esquema persistente.</div></div>` : ''}
      <div class="flags">${t.columns.length} columnas · ${t.totalCalls} operaciones DML · ${t.relations} relaciones</div>
      ${Summary(SD.summary.table(t))}

      <h3>Operaciones por tipo (INSERT / SELECT / UPDATE / DELETE…)</h3>
      ${t.ops.length ? SD.charts.tally(t.ops) : '<span class="muted">ningún objeto la usa</span>'}

      <div class="grid2">
        <div>
          <h3>Relaciones</h3>
          <div><b>Escrita por:</b> ${t.writers.length ? t.writers.map(w => `<span class="chip w" onclick="SD.app.openObject('${esc(w.object)}')">${esc(w.op)} · ${esc(w.object)}</span>`).join('') : '<span class="muted">nadie</span>'}</div>
          <div style="margin-top:6px"><b>Leída por:</b> ${t.readers.length ? t.readers.map(chip).join('') : '<span class="muted">nadie</span>'}</div>
          <div style="margin-top:6px"><b>FK → (referencia a):</b> ${t.fkOut.length ? t.fkOut.map(f => chip(f.table)).join('') : '<span class="muted">ninguna</span>'}</div>
          <div style="margin-top:6px"><b>← FK (referenciada por):</b> ${t.fkIn.length ? t.fkIn.map(f => chip(f.table)).join('') : '<span class="muted">ninguna</span>'}</div>
          ${t.triggers && t.triggers.length ? `<div style="margin-top:6px"><b>⚡ Triggers que dispara:</b> ${t.triggers.map(tr => `<span class="chip" onclick="SD.app.openObject('${esc(tr.name)}')">${esc(tr.name)}${tr.events && tr.events.length ? ` · ${esc(tr.events.join('/'))}` : ''}</span>`).join('')}</div>` : ''}
        </div>
        <div><h3>Grafo rápido</h3>${SD.charts.miniGraph(t.name, neighbors)}</div>
      </div>

      ${impactSection(t.name, DATA, impactDepth || 3)}

      <h3>Columnas (${t.columns.length})</h3>
      ${t.columns.length ? `<table class="t"><tr><th>Columna</th><th>Tipo</th><th>Null</th><th></th><th>Deriva de</th><th>Condicionado por</th></tr>${t.columns.map(colRow).join('')}</table>` : '<span class="muted">sin esquema (no se analizó su CREATE TABLE)</span>'}

      ${columnLineageSection(t, DATA)}

      ${(() => { const f = SD.risks.forComponent(DATA, t.name); return f.length ? `<h3>Riesgos de la tabla (${f.length})</h3>${FindingsTable(f, false)}` : ''; })()}
    `;
  }

  function RisksView(DATA) {
    const r = SD.risks.analyze(DATA);
    const summary = r.total === 0 ? 'No se detectaron riesgos.' :
      `Se detectaron <b>${r.total}</b> hallazgos: ` +
      ['crit', 'high', 'med', 'low', 'info'].filter(s => r.counts[s]).map(s => `<b>${r.counts[s]}</b> ${SD.risks.SEVLABEL[s].toLowerCase()}`).join(', ') + '.';

    // Distribución por severidad (donut) y por categoría (barras), luego el
    // listado agrupado por categoría.
    const sevSegs = ['crit', 'high', 'med', 'low', 'info'].filter(s => r.counts[s])
      .map(s => ({ label: SD.risks.SEVLABEL[s], value: r.counts[s], color: { crit: '#ff5252', high: '#ff8a80', med: '#ffb74d', low: '#9be7b4', info: '#999' }[s] }));
    const catBars = SD.charts.bars(SD.risks.CATS.filter(c => r.byCat[c]).map(c => ({ label: c, value: r.byCat[c].length })));

    const groups = SD.risks.CATS.filter(c => r.byCat[c]).map(c =>
      `<h3>${esc(c)} (${r.byCat[c].length})</h3>${FindingsTable(r.byCat[c], true)}`).join('');

    return `
      ${Summary(summary)}
      <div class="grid2">
        <div><h3>Por severidad</h3>${SD.charts.donut(sevSegs)}</div>
        <div><h3>Por categoría</h3>${catBars}</div>
      </div>
      ${groups || '<span class="muted">Sin hallazgos.</span>'}
    `;
  }

  // ── INVENTARIO DE DERIVADOS (columnas calculadas + variables) ────────────────
  // Todo lo que se *calcula* en un solo sitio: columnas computed de DDL (fórmula +
  // op_kinds + de qué dependen) y variables de procedimientos (construcción +
  // op_kinds, marcando las que arman SQL dinámico por concatenación). Reúne lo que
  // antes había que buscar tabla por tabla / objeto por objeto.
  function InventoryView(DATA) {
    const opk = ops => !ops || !ops.length ? '' :
      ops.map(o => `<span class="opk opk-${esc(o.split(':')[0])}" title="${esc(o)}">${esc(o.split(':')[1] || o)}</span>`).join('');
    const d = DATA.derived || { computedColumns: [], variables: [] };

    const ccRows = d.computedColumns
      .sort((a, b) => a.table.localeCompare(b.table) || a.column.localeCompare(b.column))
      .map(c => `<tr>
        <td>${DATA.byName[c.table] ? `<a href="#" onclick="SD.app.openObject('${esc(c.table)}');return false">${esc(c.table)}</a>` : esc(c.table)}.<b>${esc(c.column)}</b></td>
        <td><code class="mono">${esc(c.logic)}</code></td>
        <td>${opk(c.ops)}</td>
        <td>${c.sources.map(s => `<span class="chip muted">${esc(s.column)}</span>`).join('')}</td>
      </tr>`).join('');

    const vRows = d.variables
      .sort((a, b) => Number(b.dynamic) - Number(a.dynamic) || a.object.localeCompare(b.object))
      .map(v => `<tr>
        <td>${v.dynamic ? '⚠️ ' : ''}<b>${esc(v.name)}</b> <span class="muted">${esc(v.type || '')}</span></td>
        <td>${DATA.byName[v.object] ? `<a href="#" onclick="SD.app.openObject('${esc(v.object)}');return false">${esc(v.object)}</a>` : esc(v.object)}</td>
        <td>${opk(v.ops)}</td>
        <td>${(v.construction || []).slice(0, 3).map(s => `<code class="mono">${esc(s)}</code>`).join(' ')}</td>
      </tr>`).join('');

    return `
      <h2>🧮 Inventario de derivados</h2>
      <p class="muted">Todo lo que se <b>calcula</b> (no se almacena tal cual), con su fórmula, los <b>operadores</b> que usa (material para el motor de reglas) y de qué depende. Las variables con ⚠️ arman su valor por concatenación (típico de SQL dinámico).</p>

      <h3>Columnas calculadas (${d.computedColumns.length})</h3>
      ${d.computedColumns.length ? `<table class="t"><tr><th>Columna</th><th>Fórmula</th><th>Operadores</th><th>Depende de</th></tr>${ccRows}</table>` : '<span class="muted">Ninguna detectada (requiere DDL con columnas <code>AS (…)</code>).</span>'}

      <h3 style="margin-top:18px">Variables con cálculo / construcción (${d.variables.length})</h3>
      ${d.variables.length ? `<table class="t"><tr><th>Variable</th><th>En objeto</th><th>Operadores</th><th>Construcción (RHS)</th></tr>${vRows}</table>` : '<span class="muted">Ninguna variable con operadores o construcción registrada.</span>'}
    `;
  }

  // ── SCHEMA EXPLORER ───────────────────────────────────────────────────────────

  // Mermaid erDiagram names must be alphanumeric+underscore only.
  const erSafe = name => name.replace(/[^a-zA-Z0-9]/g, '_');

  function buildErDiagram(pinned, DATA) {
    if (!pinned || pinned.size === 0) return '';
    let out = 'erDiagram\n';
    for (const name of pinned) {
      const t = DATA.byName[name];
      if (!t || t.kind !== 'table') continue;
      const sn = erSafe(name);
      out += `  ${sn} {\n`;
      if (t.columns.length) {
        for (const col of t.columns) {
          // Mermaid erDiagram types must be a single word — strip "(size)" and spaces
          const type = (col.type || 'varchar')
            .replace(/\s*\([^)]*\)/g, '')   // remove (200), (18,2) etc.
            .replace(/[^a-zA-Z0-9_]/g, '')   // remove remaining non-word chars
            .trim() || 'varchar';
          const colName = (col.name || 'col').replace(/[^a-zA-Z0-9_]/g, '_') || 'col';
          const pk = (col.pk || col.identity) ? ' PK' : '';
          out += `    ${type} ${colName}${pk}\n`;
        }
      } else {
        out += `    varchar sin_DDL\n`;
      }
      out += `  }\n`;
    }
    // FK relationships between pinned tables only
    const seen = new Set();
    for (const name of pinned) {
      const t = DATA.byName[name];
      if (!t || t.kind !== 'table') continue;
      for (const fk of (t.fkOut || [])) {
        if (!pinned.has(fk.table)) continue;
        const key = `${name}|${fk.table}`;
        if (seen.has(key)) continue;
        seen.add(key);
        const label = (fk.constraint || '').replace(/[^a-zA-Z0-9_\s]/g, '').trim() || 'FK';
        out += `  ${erSafe(name)} }o--|| ${erSafe(fk.table)} : "${label}"\n`;
      }
    }
    return out;
  }

  function SchemaView(DATA, pinned) {
    const realTables = DATA.tables.filter(t => !t.temp);
    const notPinned  = realTables.filter(t => !pinned.has(t.name));

    const chips = [...pinned].map(name => {
      const t = DATA.byName[name];
      const neighbors = [
        ...(t ? (t.fkOut || []).map(f => f.table) : []),
        ...(t ? (t.fkIn  || []).map(f => f.table) : []),
      ].filter(n => DATA.byName[n] && DATA.byName[n].kind === 'table' && !pinned.has(n));
      const expandBtn = neighbors.length
        ? `<button class="s-btn-exp" onclick="SD.app.schemaExpand('${esc(name)}')" title="Añadir ${neighbors.length} tabla(s) relacionada(s)">+${neighbors.length}FK</button>`
        : '';
      return `<span class="schema-chip">
        <span class="schema-chip-name">${esc(name)}</span>
        ${expandBtn}
        <button class="s-btn-rm" onclick="SD.app.schemaRemove('${esc(name)}')" title="Quitar">✕</button>
      </span>`;
    }).join('');

    const addOpts = notPinned
      .map(t => `<option value="${esc(t.name)}">${esc(t.name)} (${t.columns.length}c)</option>`)
      .join('');

    const er = buildErDiagram(pinned, DATA);
    const diagram = er
      ? SD.mm.block(er, 'esquema-orm')
      : `<div class="muted" style="padding:40px;text-align:center">
           Selecciona tablas con el desplegable o haciendo clic en la lista de abajo para componer el diagrama ER.
         </div>`;

    const quickAdd = pinned.size === 0 && realTables.length
      ? `<h3 style="margin-top:20px">Tablas disponibles — clic para añadir</h3>
         <div class="schema-quick-add">
           ${realTables.map(t => `<span class="chip" style="cursor:pointer" onclick="SD.app.schemaAdd('${esc(t.name)}')">${esc(t.name)}<span class="muted" style="font-size:10px;margin-left:4px">${t.columns.length}c</span></span>`).join('')}
         </div>`
      : '';

    return `
      <h2>📐 Esquema ORM — Diagrama ER interactivo</h2>
      <div class="schema-toolbar">
        <select onchange="if(this.value){SD.app.schemaAdd(this.value);this.value=''}">
          <option value="">＋ Añadir tabla al diagrama…</option>
          ${addOpts}
        </select>
        ${chips}
        ${pinned.size > 1 ? `<button class="s-btn-clear" onclick="SD.app.schemaClear()">Limpiar todo</button>` : ''}
      </div>
      <div id="schema-er-wrap">${diagram}</div>
      <p class="schema-tip">
        💡 Haz clic en el nombre de una tabla <b>dentro del diagrama</b> para expandir sus relaciones FK ·
        Usa <b>+NFk</b> para añadir vecinas directamente · <b>✕</b> para quitar ·
        Exporta el diagrama como SVG / PNG con los botones de la barra superior
      </p>
      ${quickAdd}
    `;
  }

  // ── WORKFLOWS ─────────────────────────────────────────────────────────────────
  // Muestra las cadenas de llamada desde puntos de entrada (model.json .workflows).
  function WorkflowsView(model, DATA) {
    const workflows = (model && model.workflows) || [];
    if (!workflows.length)
      return `<h2>🔀 Workflows</h2><p class="muted">No se detectaron cadenas de llamada (ningún procedimiento de entrada llama a otros).</p>`;

    const makeChip = name =>
      DATA && DATA.byName[name]
        ? `<span class="chip" onclick="SD.app.openObject('${esc(name)}')">${esc(name)}</span>`
        : `<span class="chip muted">${esc(name)}</span>`;

    const wfHtml = workflows.map((wf, i) => {
      const pathsHtml = (wf.paths || []).map((path, j) => {
        const hops = path.hops || [];
        if (!hops.length) return '';
        const nodes = [hops[0].from, ...hops.map(h => h.to)];
        const lastHop = hops[hops.length - 1];
        const cycleNote = lastHop.cycle_back_to
          ? ` <span class="b trg" title="Ciclo: vuelve a ${esc(lastHop.cycle_back_to)}">↻</span>` : '';
        const chain = nodes.map(makeChip).join('<span class="wf-arrow">→</span>');
        return `<div class="wf-path"><span class="muted wf-idx">#${j + 1}</span>${chain}${cycleNote}</div>`;
      }).join('');

      const entryLink = DATA && DATA.byName[wf.entry_name]
        ? `<a href="#" onclick="SD.app.openObject('${esc(wf.entry_name)}');return false">${esc(wf.entry_name)}</a>`
        : `<b>${esc(wf.entry_name)}</b>`;
      const typeBadge = wf.entry_type ? ` <span class="b cc">${esc(wf.entry_type)}</span>` : '';
      const pathCount = (wf.paths || []).length;

      return `<details class="wf-entry" ${i < 5 ? 'open' : ''}>
        <summary>${entryLink}${typeBadge} <span class="muted" style="font-size:11px">${pathCount} camino(s)</span></summary>
        <div class="wf-paths">${pathsHtml || '<span class="muted">sin caminos</span>'}</div>
      </details>`;
    }).join('');

    return `
      <h2>🔀 Workflows</h2>
      <p class="muted"><b>${workflows.length}</b> punto(s) de entrada detectado(s). Máx. 30 caminos por entrada, 10 saltos de profundidad. Haz clic en cualquier nombre para navegar al objeto.</p>
      ${wfHtml}
    `;
  }

  // ── AUDITORÍA ─────────────────────────────────────────────────────────────────
  // Muestra el contenido de audit_report.json: hotspots, blind spots, patrones de riesgo.
  function AuditView(audit, DATA) {
    if (!audit) return `<h2>📋 Auditoría</h2><p class="muted">Sin datos de auditoría.</p>`;
    const s   = audit.summary          || {};
    const lc  = audit.lineage_coverage || {};
    const hs  = audit.hotspots         || [];
    const bs  = audit.blind_spots      || [];
    const ot  = audit.orphan_tables    || [];
    const rp  = audit.risk_patterns    || [];
    const imp = audit.impact           ? Object.keys(audit.impact).length : 0;
    const byType = s.by_type || {};

    const typeChips = Object.entries(byType).map(([t, n]) =>
      `<span class="chip">${esc(t)}: <b>${n}</b></span>`).join('');
    const covPct = lc.coverage_pct != null ? Math.round(lc.coverage_pct) : '?';
    const covColor = typeof lc.coverage_pct === 'number'
      ? (lc.coverage_pct >= 70 ? 'var(--ok)' : lc.coverage_pct >= 40 ? 'var(--warn)' : 'var(--chip-w)') : '';

    const objLink = name =>
      DATA && DATA.byName[name]
        ? `<a href="#" onclick="SD.app.openObject('${esc(name)}');return false">${esc(name)}</a>`
        : esc(name);

    const hsTable = hs.length ? `<table class="t">
      <tr><th>Objeto</th><th>Tipo</th><th>Score</th><th>Grado</th><th>Escribe a</th><th>Lee de</th><th>CC</th></tr>
      ${hs.slice(0, 25).map(h => `<tr>
        <td>${objLink(h.name)}</td>
        <td><span class="b cc">${esc(h.type || '')}</span></td>
        <td><b>${h.score}</b></td>
        <td>${h.degree}</td>
        <td>${(h.writes_tables || []).length}</td>
        <td>${(h.reads_tables  || []).length}</td>
        <td>${h.cyclomatic_complexity || 0}</td>
      </tr>`).join('')}
    </table>` : '<span class="muted">Sin hotspots.</span>';

    const bsTable = bs.length ? `<table class="t">
      <tr><th>Objeto</th><th>Tipo</th><th>SQL din. sin resolver</th></tr>
      ${bs.map(b => `<tr><td>${objLink(b.name)}</td><td><span class="b cc">${esc(b.type || '')}</span></td><td>${b.unresolved_dynamic_sql_steps || 0}</td></tr>`).join('')}
    </table>` : '<span class="muted">Sin objetos aislados.</span>';

    const otTable = ot.length ? `<table class="t">
      <tr><th>Tabla</th></tr>
      ${ot.map(t => `<tr><td>${objLink(t.name)}</td></tr>`).join('')}
    </table>` : '<span class="muted">Sin tablas sin referencias.</span>';

    const sevMap = { critical: 's-crit', high: 's-high', medium: 's-med' };
    const rpTable = rp.length ? `<table class="t risks">
      <tr><th>Sev.</th><th>Patrón</th><th>Objetos</th></tr>
      ${rp.map(r => {
        const cls = sevMap[r.severity] || '';
        const objs = (r.objects || []);
        const affected = objs.slice(0, 8).map(objLink).join(', ');
        const more = objs.length > 8 ? ` <span class="muted">+${objs.length - 8} más</span>` : '';
        return `<tr class="${cls}">
          <td><span class="sev ${cls}">${esc(r.severity || '')}</span></td>
          <td>${esc(r.pattern || '')}</td>
          <td>${affected}${more}</td>
        </tr>`;
      }).join('')}
    </table>` : '<span class="muted">Sin patrones de riesgo detectados.</span>';

    return `
      <h2>📋 Auditoría</h2>
      <div class="cards">
        <div class="card"><div class="n">${s.objects || 0}</div><div class="l">Objetos</div></div>
        <div class="card"><div class="n">${s.tables  || 0}</div><div class="l">Tablas</div></div>
        <div class="card"><div class="n">${hs.length}</div><div class="l">Hotspots</div></div>
        <div class="card"><div class="n">${bs.length}</div><div class="l">Sin referencias</div></div>
        <div class="card"><div class="n">${imp}</div><div class="l">Con impacto</div></div>
        <div class="card"><div class="n" ${covColor ? `style="color:${covColor}"` : ''}>${covPct}%</div><div class="l">Cobertura lineage</div></div>
      </div>
      <div class="audit-types">${typeChips}</div>

      <h3>Hotspots — objetos más conectados (${hs.length})</h3>
      ${hsTable}

      <h3>Sin referencias — aislados o solo SQL dinámico (${bs.length})</h3>
      ${bsTable}

      <h3>Tablas sin escritores ni lectores (${ot.length})</h3>
      ${otTable}

      <h3>Patrones de riesgo (${rp.length})</h3>
      ${rpTable}

      ${audit.generated_at ? `<p class="muted audit-ts">Generado: ${esc(audit.generated_at)}</p>` : ''}
    `;
  }

  SD.components = { Sidebar, Overview, ObjectView, TableView, FlowTree, FlowChartMermaid, DataFlowMermaid, Summary, RisksView, SchemaView, InventoryView, buildErDiagram, WorkflowsView, AuditView };
})(window.SD = window.SD || {});
