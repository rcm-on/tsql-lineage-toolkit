// Expresa un predicado T-SQL en el ESTÁNDAR de presentación de reglas de negocio:
// conciso, operadores preservados (=, >, <, Y, O, NO), idioms del sistema como
// etiquetas cortas (no prosa). Ej: "@Flag = 1 Y @Other = 2", "NO existe el índice
// 'CX_X'", "modificada(s): ProductID, OrderQty". Expuesto como SD.naturalize.
(function (SD) {
  const CATALOGS = {
    'sys.indexes': 'el índice', 'sys.partition_functions': 'la función de partición',
    'sys.partition_schemes': 'el esquema de partición', 'sys.procedures': 'el procedimiento',
    'sys.objects': 'el objeto', 'sys.tables': 'la tabla', 'sys.columns': 'la columna',
    'sys.types': 'el tipo', 'sys.schemas': 'el esquema', 'sys.triggers': 'el trigger',
    'sys.views': 'la vista', 'sys.databases': 'la base de datos', 'sys.foreign_keys': 'la clave foránea',
  };
  const flatten = s => (s || '').replace(/[\r\n\t]+/g, ' ').replace(/\s+/g, ' ').trim();

  function stripParens(t) {
    t = t.trim();
    while (t.startsWith('(') && t.endsWith(')')) {
      let d = 0, ok = true;
      for (let i = 0; i < t.length; i++) {
        if (t[i] === '(') d++;
        else if (t[i] === ')') { d--; if (d === 0 && i < t.length - 1) { ok = false; break; } }
      }
      if (ok) t = t.slice(1, -1).trim(); else break;
    }
    return t;
  }

  // Divide por AND/OR de primer nivel (respeta paréntesis y comillas).
  function splitTopLevel(t) {
    const parts = []; let d = 0, q = false, buf = '', op = null;
    for (let i = 0; i < t.length; i++) {
      const c = t[i];
      if (c === "'") q = !q;
      if (!q && c === '(') d++;
      else if (!q && c === ')') d--;
      else if (!q && d === 0) {
        const m = t.slice(i).match(/^\s(AND|OR)\s/i);
        if (m) { parts.push({ op, text: buf.trim() }); op = m[1].toUpperCase(); i += m[0].length - 1; buf = ''; continue; }
      }
      buf += c;
    }
    parts.push({ op, text: buf.trim() });
    return parts.filter(p => p.text);
  }

  function expr(t) {
    t = stripParens(flatten(t));

    // Cadena de UPDATE([col]) [OR/AND UPDATE([col2])…] de triggers
    if (/\bUPDATE\s*\(/i.test(t) && !/\bFROM\b/i.test(t)) {
      const cols = [...t.matchAll(/UPDATE\s*\(\s*\[?([^\])]+?)\]?\s*\)/gi)].map(m => m[1].trim());
      if (cols.length) return `${/\bNOT\b/i.test(t) ? 'NO ' : ''}modificada(s): ${cols.join(', ')}`;
    }

    // EXISTS / NOT EXISTS sobre catálogo del sistema
    let m = t.match(/(NOT\s+)?EXISTS\s*\(\s*SELECT\b[\s\S]*?\bFROM\s+(sys\.\w+)[\s\S]*?\bname\s*=\s*N?'([^']+)'/i);
    if (m) return `${m[1] ? 'NO existe' : 'existe'} ${CATALOGS[m[2].toLowerCase()] || 'el objeto'} '${m[3]}'`;

    // idioms como etiquetas cortas
    const idioms = [
      [/@@TRANCOUNT\s*>\s*0/i, 'hay transacción abierta'],
      [/@@FETCH_STATUS\s*=\s*0/i, 'hay filas en el cursor'],
      [/@@FETCH_STATUS\s*<>\s*0/i, 'fin del cursor'],
      [/@@ROWCOUNT\s*=\s*0/i, 'ninguna fila afectada'],
      [/@@ROWCOUNT\s*>\s*0/i, 'al menos una fila afectada'],
      [/ERROR_NUMBER\(\)\s+IS\s+NOT\s+NULL/i, 'hay error'],
      [/ERROR_NUMBER\(\)\s+IS\s+NULL/i, 'sin error'],
      [/XACT_STATE\(\)\s*=\s*-1/i, 'transacción no confirmable'],
      [/XACT_STATE\(\)\s*=\s*1/i, 'transacción confirmable'],
      [/SERVERPROPERTY\(\s*N?'IsXTPSupported'\s*\)\s*=\s*0/i, 'sin soporte In-Memory OLTP'],
    ];
    for (const [re, txt] of idioms) if (re.test(t)) return txt;

    // Compuesta AND/OR de primer nivel
    const parts = splitTopLevel(t);
    if (parts.length > 1)
      return parts.map((p, i) => (i === 0 ? '' : (p.op === 'OR' ? 'O ' : 'Y ')) + expr(p.text)).join(' ');

    // Comparación/predicado simple: se preserva tal cual (operadores incluidos),
    // solo se quita el prefijo N de literales y se recorta.
    t = t.replace(/\bN'/g, "'");
    return t.length > 90 ? t.slice(0, 88) + '…' : t;
  }

  SD.naturalize = function (condType, condText) {
    if (condType === 'CATCH') return 'AL OCURRIR UN ERROR (CATCH)';
    if (condType === 'UNCONDITIONAL') return 'SIEMPRE';
    if (condType === 'WHILE') return 'MIENTRAS ' + expr(condText);
    if (condType === 'IF_ELSE') {
      const m = flatten(condText).match(/^NOT\s*\(([\s\S]*)\)$/);
      return m ? 'NO (' + expr(m[1]) + ')' : 'NO (' + expr(condText) + ')';
    }
    return expr(condText); // IF
  };
})(window.SD = window.SD || {});
