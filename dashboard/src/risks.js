// Analizador de malas prácticas / riesgos sobre el modelo ya shaped (sin tocar el
// parser). Reglas basadas en catálogos de anti-patrones de T-SQL / SQL Server
// (Simple Talk "40 problems", SQLBlog best-practices checklist, Brent Ozar).
// Cada hallazgo lleva severidad + categoría, para listarlo y agruparlo tanto a
// nivel general como por componente. Expuesto como SD.risks.
(function (SD) {
  const SEV = { crit: 0, high: 1, med: 2, low: 3, info: 4 };
  const SEVLABEL = { crit: 'CRÍTICO', high: 'ALTO', med: 'MEDIO', low: 'BAJO', info: 'INFO' };
  // Categorías: Seguridad, Robustez, Rendimiento, Mantenibilidad, Integridad, Diseño
  const CATS = ['Seguridad', 'Robustez', 'Rendimiento', 'Mantenibilidad', 'Integridad', 'Diseño'];

  // Profundidad de anidación del árbol de control (cadena de condiciones más larga).
  function treeDepth(nodes, d = 0) {
    let max = d;
    for (const n of (nodes || [])) if (n.kind === 'cond') max = Math.max(max, treeDepth(n.children, d + 1));
    return max;
  }
  // Aplana el árbol de control a la lista de steps (hojas kind==='step'), entrando
  // recursivamente en los nodos de condición. Para reglas sentencia a sentencia
  // (UPDATE/DELETE sin WHERE, SELECT *).
  function collectSteps(nodes, acc) {
    acc = acc || [];
    for (const n of (nodes || [])) {
      if (n.kind === 'step') acc.push(n);
      else if (n.kind === 'cond') collectSteps(n.children, acc);
    }
    return acc;
  }
  // Nombre "plano" del objeto sin esquema (para detectar prefijo sp_).
  const leaf = name => (name || '').split('.').pop();

  function analyze(DATA) {
    const out = [];
    const add = (sev, cat, rule, component, detail) => out.push({ sev, cat, rule, component, detail });

    for (const o of DATA.objects) {
      // ── Seguridad ────────────────────────────────────────────
      const tainted = o.vars.filter(v => v.buildsSql > 0 && v.assignedFrom.length > 0);
      if (tainted.length)
        add('crit', 'Seguridad', 'Inyección SQL', o.name, `SQL dinámico construido desde datos de tabla: ${tainted.map(v => `${v.name} ← ${v.assignedFrom.join(', ')}`).join('; ')}`);
      else if (o.dyn > 0) {
        const dynVars = o.vars.filter(v => v.buildsSql > 0).map(v => v.name);
        add('high', 'Seguridad', 'SQL dinámico', o.name, `Ejecuta ${o.dyn} sentencia(s) de SQL dinámico${dynVars.length ? ` (desde ${dynVars.join(', ')})` : ''}: revisar parametrización/permisos.`);
      }

      // ── Robustez (transacciones / errores) ───────────────────
      if (o.hasCursor && o.hasTx && !o.hasErr)
        add('high', 'Robustez', 'Cursor en transacción sin TRY/CATCH', o.name, 'Cursor dentro de transacción sin manejo de errores: alto riesgo de transacción/recurso huérfano.');
      else if (o.hasTx && !o.hasErr)
        add('high', 'Robustez', 'Transacción sin TRY/CATCH', o.name, 'Abre transacción sin manejo de errores: riesgo de transacción huérfana / bloqueo si falla.');

      if (o.writes.length && !o.hasTx && !o.hasErr)
        add('med', 'Robustez', 'Escritura sin protección', o.name, `Modifica datos (${[...new Set(o.writes.map(w => w.table))].slice(0, 3).join(', ')}) sin transacción ni manejo de errores.`);

      // ── Rendimiento ──────────────────────────────────────────
      if (o.hasCursor)
        add('med', 'Rendimiento', 'Uso de cursor', o.name, 'Cursor explícito: revisar si puede resolverse con operaciones de conjunto.');

      if (/^sp_/i.test(leaf(o.name)))
        add('med', 'Rendimiento', 'Prefijo sp_', o.name, 'El prefijo sp_ hace que SQL Server busque primero en master (penalización) y puede colisionar con procedimientos de sistema.');

      for (const w of o.writesByTable || [])
        if (w.count >= 3)
          add('med', 'Rendimiento', 'Escrituras repetidas a la misma tabla', o.name, `Escribe ${w.count} veces en ${w.table}: posible candidato a consolidar en una sola operación de conjunto.`);

      const allSteps = collectSteps(o.flow);
      if (allSteps.some(s => s.selectStar))
        add('low', 'Rendimiento', 'SELECT *', o.name, 'Usa SELECT *: trae columnas de más, rompe ante cambios de esquema e impide cubrir consultas con índices. Listar columnas explícitas.');

      // ── Integridad (operaciones destructivas) ────────────────
      const noWhere = allSteps.filter(s => (s.action === 'UPDATE' || s.action === 'DELETE') && (!s.filters || s.filters.length === 0));
      if (noWhere.length)
        add('high', 'Integridad', 'UPDATE/DELETE sin WHERE', o.name, `Modifica/borra sin filtro: ${[...new Set(noWhere.map(s => `${s.action} ${s.target}`))].join('; ')}. Afecta a TODAS las filas de la tabla.`);

      const truncates = (o.writes || []).filter(w => w.op === 'TRUNCATE');
      if (truncates.length)
        add('med', 'Integridad', 'TRUNCATE de tabla', o.name, `TRUNCATE TABLE ${[...new Set(truncates.map(w => w.table))].join(', ')}: borrado masivo sin log por fila, reinicia IDENTITY y no respeta WHERE/triggers.`);

      // ── Mantenibilidad ───────────────────────────────────────
      if (o.complexity >= 10)
        add('med', 'Mantenibilidad', 'Complejidad alta', o.name, `Complejidad ciclomática ${o.complexity} (difícil de mantener/probar).`);
      else if (o.complexity >= 6)
        add('low', 'Mantenibilidad', 'Complejidad moderada', o.name, `Complejidad ciclomática ${o.complexity}.`);

      const depth = treeDepth(o.flow);
      if (depth >= 4)
        add('med', 'Mantenibilidad', 'Anidación profunda', o.name, `Anidación de control de ${depth} niveles: lógica difícil de seguir (considerar guardas/extraer procedimientos).`);
      else if (depth === 3)
        add('low', 'Mantenibilidad', 'Anidación profunda', o.name, `Anidación de control de ${depth} niveles.`);

      const unused = o.vars.filter(v => v.usedBy === 0 && v.buildsSql === 0 && v.assignedFrom.length === 0);
      if (unused.length)
        add('low', 'Mantenibilidad', 'Variable sin uso', o.name, `Variables declaradas y nunca usadas: ${unused.map(v => v.name).join(', ')}.`);

      if (o.callsIn.length === 0 && o.writes.length === 0 && o.steps > 0 && /\bufn|\bfn|function/i.test(o.name))
        add('low', 'Mantenibilidad', 'Posible código muerto', o.name, 'Función sin llamadores detectados (puede usarse en vistas/columnas calculadas no analizadas, o estar obsoleta).');

      // ── Diseño ───────────────────────────────────────────────
      const distinctWrites = [...new Set(o.writes.map(w => w.table))];
      if (distinctWrites.length >= 5)
        add('med', 'Diseño', 'Objeto hace demasiado', o.name, `Escribe en ${distinctWrites.length} tablas distintas (${distinctWrites.slice(0, 4).join(', ')}…): candidato a dividir.`);

      if (o.parseError)
        add('info', 'Mantenibilidad', 'Error de parseo', o.name, o.parseError);
    }

    // ── Riesgos a nivel de TABLA ───────────────────────────────
    for (const t of (DATA.tables || [])) {
      if (t.columns.length > 0 && !t.columns.some(c => c.pk))
        add('med', 'Integridad', 'Tabla sin clave primaria', t.name, `${t.columns.length} columnas y ninguna PK: riesgo de duplicados y replicación/merge problemáticos.`);

      if (t.writers.length >= 4)
        add('med', 'Diseño', 'Acoplamiento alto (tabla)', t.name, `Escrita por ${t.writers.length} objetos: cualquier cambio de esquema impacta a muchos.`);

      if (t.writers.length > 0 && t.readers.length === 0 && t.columns.length > 0)
        add('low', 'Diseño', 'Tabla escrita pero nunca leída', t.name, 'Recibe escrituras pero ningún objeto analizado la lee (staging, auditoría o dato muerto).');

      if (t.columns.length > 0 && t.columns.every(c => c.nullable))
        add('low', 'Integridad', 'Tabla totalmente anulable', t.name, `Las ${t.columns.length} columnas admiten NULL: sin NOT NULL ni PK que garantice una fila identificable y con datos mínimos.`);

      if (t.columns.length >= 12)
        add('low', 'Diseño', 'Tabla ancha', t.name, `${t.columns.length} columnas: posible tabla "Dios" (mezcla responsabilidades), candidata a normalizar/dividir.`);
    }

    out.sort((a, b) => SEV[a.sev] - SEV[b.sev] || a.cat.localeCompare(b.cat) || a.component.localeCompare(b.component));
    const counts = { crit: 0, high: 0, med: 0, low: 0, info: 0 };
    const byCat = {};
    for (const f of out) { counts[f.sev]++; (byCat[f.cat] = byCat[f.cat] || []).push(f); }
    return { findings: out, counts, byCat, total: out.length };
  }

  // Hallazgos de un componente concreto (objeto o tabla), por nombre.
  function forComponent(DATA, name) {
    return analyze(DATA).findings.filter(f => f.component === name);
  }

  SD.risks = { analyze, forComponent, SEVLABEL, CATS };
})(window.SD = window.SD || {});
