// Mini gráficos en SVG/HTML inline, sin librerías externas. Expuesto como SD.charts.
(function (SD) {
  const esc = s => (s == null ? '' : ('' + s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'));
  const PALETTE = ['#e91e8c', '#4caf50', '#2196f3', '#ff9800', '#9c27b0', '#00bcd4', '#cddc39', '#795548'];

  // Barras horizontales: items = [{label, value, color?, onClick?}]
  function bars(items, opts = {}) {
    const max = Math.max(...items.map(i => i.value), 1);
    return `<div class="chart-bars">` + items.map(i => {
      const w = Math.round(i.value / max * 100);
      const click = i.onClick ? ` onclick="${i.onClick}"` : '';
      return `<div class="bar"><span class="lbl"${click}>${esc(i.label)}</span>` +
        `<span class="track"><span class="fill" style="width:${w}%;background:${i.color || 'var(--ac)'}"></span></span>` +
        `<span class="v">${esc(i.value)}</span></div>`;
    }).join('') + `</div>`;
  }

  // Donut SVG: segs = [{label, value, color?}]
  function donut(segs, opts = {}) {
    const size = opts.size || 132, r = size / 2 - 12, cx = size / 2, cy = size / 2, C = 2 * Math.PI * r;
    const total = segs.reduce((a, s) => a + s.value, 0) || 1;
    let off = 0;
    const rings = segs.map((s, k) => {
      const frac = s.value / total, len = frac * C;
      const el = `<circle r="${r}" cx="${cx}" cy="${cy}" fill="none" stroke="${s.color || PALETTE[k % PALETTE.length]}" ` +
        `stroke-width="12" stroke-dasharray="${len} ${C - len}" stroke-dashoffset="${-off}" transform="rotate(-90 ${cx} ${cy})"></circle>`;
      off += len; return el;
    }).join('');
    const legend = segs.map((s, k) => `<div class="lg"><span class="dot" style="background:${s.color || PALETTE[k % PALETTE.length]}"></span>${esc(s.label)} <b>${esc(s.value)}</b></div>`).join('');
    return `<div class="chart-donut"><svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">${rings}` +
      `<text x="${cx}" y="${cy}" text-anchor="middle" dominant-baseline="central" class="donut-c">${total}</text></svg>` +
      `<div class="legend">${legend}</div></div>`;
  }

  // Mini distribución por categoría (chips con conteo): cuenta acciones de un flow plano.
  function tally(pairs, opts = {}) {
    const max = Math.max(...pairs.map(p => p[1]), 1);
    return `<div class="chart-tally">` + pairs.map(([k, v], i) => {
      const h = 8 + Math.round(v / max * 34);
      return `<div class="col" title="${esc(k)}: ${v}"><span class="colbar" style="height:${h}px;background:${PALETTE[i % PALETTE.length]}"></span><span class="colv">${v}</span><span class="colk">${esc(k)}</span></div>`;
    }).join('') + `</div>`;
  }

  // Mini-grafo de flujo izquierda → derecha: entradas a la izquierda entrando al
  // objetivo central, salidas a la derecha saliendo de él, agrupadas por tipo y nombre.
  // neighbors = [{label, dir:'in'|'out', color?, role?, onClick?}]
  // dir 'in'  = el vecino apunta al centro (le llama / le escribe / le lee) → columna izquierda.
  // dir 'out' = el centro apunta al vecino (llama a / FK →)               → columna derecha.
  function miniGraph(center, neighbors, opts = {}) {
    const ins = neighbors.filter(n => n.dir === 'in');
    const outs = neighbors.filter(n => n.dir !== 'in');
    const NW = 138, NH = 26, ROW = 38, PTOP = 34, PBOT = 14, CW = 150, CH = 46;
    const rows = Math.max(ins.length, outs.length, 1);
    const h = PTOP + PBOT + rows * ROW;
    const w = opts.w || 560, cx = w / 2, cy = PTOP + (rows * ROW) / 2 - ROW / 2;
    const lx = NW / 2 + 14, rx = w - NW / 2 - 14;          // centros X de cada columna
    const cl = cx - CW / 2, crt = cx + CW / 2;             // bordes del nodo central

    // Y centrada del item i de una columna de `count` elementos.
    const colY = (count, i) => cy - ((count - 1) * ROW) / 2 + i * ROW;

    const sideNode = (nb, x, y, side) => {
      const col = nb.color || (side === 'in' ? '#9cdcfe' : '#4caf50');
      const click = nb.onClick ? ` onclick="${nb.onClick}" style="cursor:pointer"` : '';
      const role = nb.role ? `<text x="${x}" y="${y - NH / 2 - 3}" text-anchor="middle" class="mg-role">${esc(nb.role)}</text>` : '';
      // flecha: in = nodo→centro ; out = centro→nodo
      const edge = side === 'in'
        ? `<line x1="${x + NW / 2}" y1="${y}" x2="${cl}" y2="${cy}" stroke="${col}" stroke-width="1.5" marker-end="url(#arr)"></line>`
        : `<line x1="${crt}" y1="${cy}" x2="${x - NW / 2}" y2="${y}" stroke="${col}" stroke-width="1.5" marker-end="url(#arr)"></line>`;
      return edge + `<g${click}>` +
        `<rect x="${x - NW / 2}" y="${y - NH / 2}" width="${NW}" height="${NH}" rx="5" fill="#2d2d2d" stroke="${col}"></rect>` +
        `<text x="${x}" y="${y}" text-anchor="middle" dominant-baseline="central" class="mg-n">${esc(short(nb.label))}</text>` +
        `<title>${esc(nb.label)}${nb.role ? ' · ' + esc(nb.role) : ''}</title></g>` + role;
    };

    if (!neighbors.length)
      return `<div class="mini-graph"><svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">` +
        `<rect x="${cl}" y="${cy - CH / 2}" width="${CW}" height="${CH}" rx="7" fill="#37373d" stroke="var(--ac)" stroke-width="2"></rect>` +
        `<text x="${cx}" y="${cy}" text-anchor="middle" dominant-baseline="central" class="mg-c">${esc(short(center))}</text>` +
        `<text x="${cx}" y="${cy + CH / 2 + 16}" text-anchor="middle" class="mg-empty">sin conexiones</text></svg></div>`;

    const headers =
      (ins.length ? `<text x="${lx}" y="18" text-anchor="middle" class="mg-hdr">▸ Entradas (${ins.length})</text>` : '') +
      (outs.length ? `<text x="${rx}" y="18" text-anchor="middle" class="mg-hdr">Salidas (${outs.length}) ▸</text>` : '');
    const left = ins.map((nb, i) => sideNode(nb, lx, colY(ins.length, i), 'in')).join('');
    const right = outs.map((nb, i) => sideNode(nb, rx, colY(outs.length, i), 'out')).join('');

    return `<div class="mini-graph"><svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">` +
      `<defs><marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L7,3 L0,6 Z" fill="#888"></path></marker></defs>` +
      `${headers}${left}${right}` +
      `<rect x="${cl}" y="${cy - CH / 2}" width="${CW}" height="${CH}" rx="7" fill="#37373d" stroke="var(--ac)" stroke-width="2"></rect>` +
      `<text x="${cx}" y="${cy}" text-anchor="middle" dominant-baseline="central" class="mg-c">${esc(short(center))}</text></svg></div>`;
  }
  const short = s => { s = '' + s; const t = s.split('.').pop(); return t.length > 16 ? t.slice(0, 15) + '…' : t; };

  SD.charts = { bars, donut, tally, miniGraph, PALETTE, esc };
})(window.SD = window.SD || {});
