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

  function FlowTree(nodes, byName) {
    return nodes.map(n => {
      if (n.kind === 'cond')
        return `<details open><summary class="cond">${esc(n.label)}</summary>${FlowTree(n.children, byName)}</details>`;
      const tgt = n.target ? ` <span class="tgt">→ ${esc(n.target)}</span>` : '';
      const from = (n.sqlFrom && n.sqlFrom.length) ? ` ← ${esc(n.sqlFrom.join(', '))}` : '';
      const dyn = n.dynamic ? ` <span class="dyn">[SQL dinámico${from}]</span>` : '';
      const runs = n.dynSql ? ` <span class="dyn">⚡ ejecuta: ${esc(n.dynSql)}</span>` : '';
      const link = byName[n.target] ? ` <a href="#" onclick="SD.app.openObject('${esc(n.target)}');return false" class="muted" title="ir">↗</a>` : '';
      const detail = n.detail ? ` <span class="muted">(${esc(n.detail)})</span>` : '';
      return `<div class="step"><span class="act ${actClass(n.action)}">${esc(n.action)}</span>${detail}${tgt}${dyn}${runs}${link} <span class="muted">· L${n.line}</span></div>`;
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
          const txt = node.action + (node.detail ? ` (${node.detail})` : '') + tgt + run + dyn + (node.line ? ` · L${node.line}` : '');
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
    for (const t of o.reads) inputs.push({ label: t, link: t });
    const outputs = [];
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

  function paramTable(title, ps) {
    if (!ps.length) return '';
    return `<h3>${title} (${ps.length})</h3><table class="t"><tr><th>Nombre</th><th>Tipo</th></tr>` +
      ps.map(p => `<tr><td>${esc(p.name)}</td><td>${esc(p.type)}</td></tr>`).join('') + `</table>`;
  }

  function ObjectView(o, DATA) {
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
    return `
      <h2>${esc(o.name)}</h2>
      <div class="flags">tx=${o.hasTx} · errores=${o.hasErr} · cursor=${o.hasCursor}</div>
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
      ${o.flow.length ? `<details class="tree-fold"><summary>Ver como árbol de texto (condiciones en lenguaje natural)</summary>
      <div class="tree">${FlowTree(o.flow, DATA.byName)}</div></details>` : ''}

      ${(() => { const df = DataFlowMermaid(o, DATA); return df ? `<h3>Flujo de datos</h3>${SD.mm.block(df, 'Flujo de datos')}` : ''; })()}

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

  function TableView(t, DATA) {
    const chip = n => DATA.byName[n] ? `<span class="chip" onclick="SD.app.openObject('${esc(n)}')">${esc(n)}</span>` : `<span class="chip muted">${esc(n)}</span>`;
    const colRow = c => `<tr><td>${c.pk ? '🔑 ' : ''}${esc(c.name)}</td><td>${esc(c.type)}</td><td>${c.nullable ? '' : 'NOT NULL'}</td><td>${c.identity ? 'IDENTITY' : ''}</td></tr>`;
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
        </div>
        <div><h3>Grafo rápido</h3>${SD.charts.miniGraph(t.name, neighbors)}</div>
      </div>

      <h3>Columnas (${t.columns.length})</h3>
      ${t.columns.length ? `<table class="t"><tr><th>Columna</th><th>Tipo</th><th>Null</th><th></th></tr>${t.columns.map(colRow).join('')}</table>` : '<span class="muted">sin esquema (no se analizó su CREATE TABLE)</span>'}

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

  SD.components = { Sidebar, Overview, ObjectView, TableView, FlowTree, FlowChartMermaid, DataFlowMermaid, Summary, RisksView };
})(window.SD = window.SD || {});
