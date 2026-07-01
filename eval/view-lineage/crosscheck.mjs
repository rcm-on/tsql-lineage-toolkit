#!/usr/bin/env node
// Cross-check del lineage de VISTAS: compara la extracción del toolkit (nodestore)
// contra el ground-truth nativo de SQL Server (ground-truth.csv, ver extract-truth.sql).
//
// Para cada vista comprueba 3 cosas:
//   src_cols   reads_column + filters_on   ==  ground-truth src_cols   (columnas fuente)
//   src_tables reads_from                  ==  ground-truth src_tables (tablas base)
//   out_cols   columnas de salida modeladas ==  ground-truth out_cols  (deben ser :Column nodos)
//
// Uso:  node crosscheck.mjs [nodestore_dir]   (def: ../../out/graph_full.nodes)
// Sale !=0 si hay discrepancias (apto como gate de CI).
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const store = process.argv[2] || join(here, '..', '..', 'out', 'graph_full.nodes');

const rows = readFileSync(join(here, 'ground-truth.csv'), 'utf8')
  .trim().split('\n').slice(1)
  .map(l => { const [db, view, out, src, tbl] = l.split(','); return { db, view, out: +out, src: +src, tbl: +tbl }; });

const count = (edges, t) => edges.filter(e => e.type === t).length;

function toolkitView(db, view) {
  const f = join(store, 'objects', `${db}_${view}`, 'object.json');
  if (!existsSync(f)) return null;
  const o = JSON.parse(readFileSync(f, 'utf8').replace(/^﻿/, ''));
  const eo = o.edges_out || [];
  // columnas de salida modeladas = nodos :Column distintos que cuelgan de la propia vista
  // (start de un DERIVES_FROM via_view, o destino de un HAS_COLUMN desde la vista).
  const outCols = new Set();
  // Columnas fuente = columnas DISTINTAS leídas o filtradas. El oráculo
  // (sys.dm_sql_referenced_entities, minor_id>0) reporta cada columna referenciada
  // UNA vez; una columna que se proyecta Y se usa en un JOIN/WHERE genera dos aristas
  // (READS_COLUMN + FILTERS_ON) pero es una sola columna fuente -> deduplicar por destino.
  const srcCols = new Set();
  for (const e of eo) {
    if (e.type === 'DERIVES_FROM' && e.props?.via_view) outCols.add(e.from);
    if (e.type === 'HAS_COLUMN') outCols.add(e.to);
    if (e.type === 'READS_COLUMN' || e.type === 'FILTERS_ON') srcCols.add(e.to);
  }
  return {
    src: srcCols.size,
    tbl: count(eo, 'READS_FROM'),
    out: outCols.size,
  };
}

const pad = (s, n) => String(s).padEnd(n);
const mark = (a, b) => (a === b ? 'OK ' : 'DIFF');
let fails = 0, missing = 0, checked = 0;

console.log(pad('VIEW', 44), pad('src(tk/gt)', 14), pad('tbl(tk/gt)', 14), 'out(tk/gt)');
console.log('-'.repeat(92));
for (const r of rows) {
  const tk = toolkitView(r.db, r.view);
  if (!tk) { console.log(pad(`${r.db}/${r.view}`, 44), '(sin nodo en el nodestore — BD no procesada)'); missing++; continue; }
  checked++;
  const sOK = mark(tk.src, r.src), tOK = mark(tk.tbl, r.tbl), oOK = mark(tk.out, r.out);
  if (sOK === 'DIFF' || tOK === 'DIFF' || oOK === 'DIFF') fails++;
  console.log(
    pad(r.view, 44),
    pad(`${sOK} ${tk.src}/${r.src}`, 14),
    pad(`${tOK} ${tk.tbl}/${r.tbl}`, 14),
    `${oOK} ${tk.out}/${r.out}`);
}
console.log('-'.repeat(92));
console.log(`Comprobadas: ${checked}   Sin nodestore: ${missing}   Vistas con discrepancia: ${fails}`);
const gtOut = rows.reduce((n, r) => n + r.out, 0);
console.log(`Columnas de salida esperadas (todas las BD): ${gtOut}  — hoy modeladas como nodos: 0`);
process.exit(fails > 0 ? 1 : 0);
