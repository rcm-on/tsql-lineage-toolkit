// Cadena de impacto multi-nivel: BFS por niveles sobre el modelo ya cargado
// en DATA (shape.js), sin tocar el JSON ni el backend. Para un objeto o
// tabla raíz, sigue tanto CALLS (entre objetos) como reads/writes (objeto
// <-> tabla) en ambas direcciones:
//   downstream (qué afecta este nodo): objeto -> callsOut/writes, tabla -> readers
//   upstream   (qué alimenta este nodo): objeto -> callsIn/reads,   tabla -> writers
// Expuesto como SD.impact.chain(rootName, DATA, maxDepth).
(function (SD) {
  const MAX_PER_LEVEL = 8;

  function neighborsOf(name, DATA, dir) {
    const e = DATA.byName[name];
    if (!e) return [];
    if (e.kind === 'object') {
      if (dir === 'down') {
        return e.callsOut.map(n => ({ name: n, kind: 'object', label: 'EXEC' }))
          .concat(e.writes.map(w => ({ name: w.table, kind: 'table', label: w.op })));
      }
      return e.callsIn.map(n => ({ name: n, kind: 'object', label: 'EXEC' }))
        .concat(e.reads.map(n => ({ name: n, kind: 'table', label: 'lee' })));
    }
    // tabla
    if (dir === 'down')
      return e.readers.map(n => ({ name: n, kind: 'object', label: 'lee' }));
    return e.writers.map(w => ({ name: w.object, kind: 'object', label: w.op }));
  }

  // BFS por niveles en una sola dirección ('down' = niveles positivos, 'up' = negativos).
  // visited: Map<name, depth> compartido entre upstream/downstream para no expandir
  // dos veces el mismo nodo (pero sí conservar aristas nuevas hacia un nodo ya visto).
  function walk(rootName, DATA, dir, maxDepth, visited) {
    const levels = [];
    let frontier = [rootName];
    const edges = [];
    for (let depth = 1; depth <= maxDepth; depth++) {
      const levelNodes = [];
      const seenThisLevel = new Set();
      let truncated = false;
      for (const fromName of frontier) {
        const neighbors = neighborsOf(fromName, DATA, dir);
        for (const nb of neighbors) {
          const already = visited.has(nb.name);
          if (!already) {
            if (seenThisLevel.size >= MAX_PER_LEVEL) { truncated = true; continue; }
            visited.set(nb.name, depth);
            seenThisLevel.add(nb.name);
            levelNodes.push({ name: nb.name, kind: nb.kind });
          }
          // Deliberadamente NO usamos la convención "Objeto -> Tabla" de la acción
          // aquí (a diferencia de la mini-tabla y DataFlowMermaid): este diagrama
          // se lee como una línea de tiempo causal izquierda->derecha (Nivel -N
          // = upstream, Nivel +N = downstream), y Mermaid posiciona los nodos
          // según la dirección de las aristas. Forzar "lee" en sentido Objeto->Tabla
          // (probado y revertido dos veces) descoloca el nodo raíz fuera de su
          // columna y rompe el layout por completo cuando la tabla leída es
          // upstream. Aquí gana la lectura de niveles sobre la dirección del verbo.
          const a = dir === 'down' ? fromName : nb.name;
          const b = dir === 'down' ? nb.name : fromName;
          edges.push({ from: a, to: b, label: nb.label });
        }
      }
      if (truncated) levelNodes.push({ name: `…+más (nivel ${dir === 'down' ? depth : -depth})`, kind: 'more' });
      if (!levelNodes.length) break;
      levels.push(levelNodes);
      frontier = levelNodes.filter(n => n.kind !== 'more').map(n => n.name);
      if (!frontier.length) break;
    }
    return { levels, edges };
  }

  function chain(rootName, DATA, maxDepth) {
    const root = DATA.byName[rootName];
    if (!root) return { levels: [], edges: [] };
    const visited = new Map([[rootName, 0]]);
    const down = walk(rootName, DATA, 'down', maxDepth, visited);
    const up = walk(rootName, DATA, 'up', maxDepth, visited);

    // levels: [...up reversed (más lejano primero), [root], ...down]
    const levels = [...up.levels.slice().reverse(), [{ name: rootName, kind: root.kind }], ...down.levels];
    const edges = [...up.edges, ...down.edges];
    return { levels, edges, upLevels: up.levels.length, downLevels: down.levels.length };
  }

  SD.impact = { chain };
})(window.SD = window.SD || {});
