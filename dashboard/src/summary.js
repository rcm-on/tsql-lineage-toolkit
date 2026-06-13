// Genera mini-resúmenes en lenguaje natural para no tener que pedirlos: uno de la
// base entera y uno por cada objeto al abrirlo. Expuesto como SD.summary.
(function (SD) {
  function db(DATA) {
    const g = DATA.general;
    const top = g.topComplexity[0];
    const hot = g.hotspotWrites[0];
    const parts = [
      `Esta base tiene <b>${g.totalObjects}</b> objetos programables.`,
      `<b>${g.withDyn}</b> usan SQL dinámico${g.withDyn ? ' (revisar superficie de inyección)' : ''},`,
      `<b>${g.withTx}</b> manejan transacciones y <b>${g.withErr}</b> tienen manejo de errores.`,
      top ? `El más complejo es <b>${top.name}</b> (cc=${top.cc}).` : '',
      hot ? `La tabla más escrita es <b>${hot.table}</b> (${hot.count} escrituras).` : '',
      g.parseErrors ? `⚠ <b>${g.parseErrors}</b> con error de parseo.` : '',
    ];
    return parts.filter(Boolean).join(' ');
  }

  function object(o) {
    const kind = o.steps === 0 ? 'objeto sin pasos analizables'
      : (o.hasCursor ? 'rutina con cursor' : 'rutina');
    const bits = [`Es una <b>${kind}</b> con complejidad <b>${o.complexity}</b> y <b>${o.steps}</b> pasos.`];

    if (o.callsOut.length) bits.push(`Llama a <b>${o.callsOut.length}</b> objeto(s): ${o.callsOut.slice(0, 4).join(', ')}${o.callsOut.length > 4 ? '…' : ''}.`);
    if (o.callsIn.length) bits.push(`La invocan <b>${o.callsIn.length}</b> objeto(s).`);
    else bits.push(`No la llama nadie directamente${o.steps ? ' (posible trigger o punto de entrada)' : ''}.`);

    if (o.writes.length) bits.push(`Escribe en ${[...new Set(o.writes.map(w => w.table))].slice(0, 4).join(', ')}.`);
    if (o.reads.length) bits.push(`Lee de ${o.reads.slice(0, 4).join(', ')}.`);

    const dynVars = o.vars.filter(v => v.buildsSql > 0);
    if (dynVars.length) bits.push(`⚠ Construye SQL dinámico desde ${dynVars.map(v => v.name).join(', ')}.`);
    if (o.hasTx && o.hasErr) bits.push(`Usa transacción con manejo de errores (TRY/CATCH).`);
    if (o.parseError) bits.push(`⚠ Error de parseo: ${o.parseError}.`);
    return bits.join(' ');
  }

  function table(t) {
    const bits = [`Es una <b>tabla</b> con <b>${t.columns.length}</b> columna(s)${t.columns.filter(c => c.pk).length ? ` (PK: ${t.columns.filter(c => c.pk).map(c => c.name).join(', ')})` : ''}.`];
    if (t.totalCalls) bits.push(`Recibe <b>${t.totalCalls}</b> operación(es): ${t.ops.map(([k, v]) => `${v} ${k}`).join(', ')}.`);
    else bits.push(`Ningún objeto analizado la usa.`);
    if (t.writers.length) bits.push(`La escriben <b>${t.writers.length}</b> objeto(s)${t.readers.length ? ` y la leen <b>${t.readers.length}</b>` : ''}.`);
    if (t.fkOut.length) bits.push(`Referencia (FK) a ${t.fkOut.map(f => f.table).join(', ')}.`);
    if (t.fkIn.length) bits.push(`Es referenciada por <b>${t.fkIn.length}</b> tabla(s).`);
    return bits.join(' ');
  }

  SD.summary = { db, object, table };
})(window.SD = window.SD || {});
