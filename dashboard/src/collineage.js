// Linaje transitivo a nivel de COLUMNA (profundidad + cadena ordenada), el
// equivalente en el dashboard de las queries @col_impact / @col_provenance de
// graph.db. Camina la adyacencia DERIVES_FROM precomputada en DATA.colAdj
// (shape.js) desde una columna raíz:
//   provenance (up)  = de qué se computa, target -> sources   (@col_provenance)
//   impact     (down)= qué se rompe si cambia, source -> targets (@col_impact)
// Devuelve los nodos alcanzados con su profundidad mínima (hops) y la cadena
// ordenada raíz -> ... -> nodo, con guarda anti-ciclos sobre la ruta visitada.
(function (SD) {
  const MAX_DEPTH = 20;

  // dir: 'up' (provenance) | 'down' (impact). Devuelve [{key, table, column,
  // hops, ops, logic, chain:[{table,column}...]}] ordenado por hops.
  function walk(rootKey, colAdj, dir, maxDepth) {
    const adj = colAdj[dir] || {};
    const best = new Map();          // key -> {hops, ops, logic, chain}
    const rootInfo = colAdj.info[rootKey] || { table: '', column: rootKey };
    // BFS: frontera de {key, depth, path[]}. path lleva las keys para cortar ciclos.
    let frontier = [{ key: rootKey, depth: 0, chain: [rootInfo], pathKeys: new Set([rootKey]) }];
    const limit = Math.min(maxDepth || MAX_DEPTH, MAX_DEPTH);

    while (frontier.length) {
      const next = [];
      for (const cur of frontier) {
        if (cur.depth >= limit) continue;
        for (const e of (adj[cur.key] || [])) {
          if (cur.pathKeys.has(e.key)) continue;        // ciclo: ya en esta ruta
          const depth = cur.depth + 1;
          const chain = cur.chain.concat([{ table: e.table, column: e.column }]);
          const prev = best.get(e.key);
          if (!prev || depth < prev.hops) {
            best.set(e.key, { key: e.key, table: e.table, column: e.column, hops: depth, ops: e.ops || [], logic: e.logic || '', computed: e.computed, chain });
          }
          const pk = new Set(cur.pathKeys); pk.add(e.key);
          next.push({ key: e.key, depth, chain, pathKeys: pk });
        }
      }
      frontier = next;
    }
    return [...best.values()].sort((a, b) => a.hops - b.hops || a.column.localeCompare(b.column));
  }

  const keyOf = (table, column) => `${(table || '').toLowerCase()}.${(column || '').toLowerCase()}`;

  // API: provenance (de qué deriva) e impact (qué deriva de ella) para una columna.
  function provenance(table, column, DATA, maxDepth) {
    return walk(keyOf(table, column), DATA.colAdj, 'up', maxDepth);
  }
  function impact(table, column, DATA, maxDepth) {
    return walk(keyOf(table, column), DATA.colAdj, 'down', maxDepth);
  }

  SD.collineage = { provenance, impact, keyOf };
})(window.SD = window.SD || {});
