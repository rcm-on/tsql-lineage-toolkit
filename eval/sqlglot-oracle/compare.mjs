#!/usr/bin/env node
// Cruza el lineage de columna de NUESTRO motor contra sqlglot (oráculo independiente) para
// el subconjunto de CONSULTAS (SELECT). Cada caso en cases/*.sql es un SELECT puro: se
// envuelve como CREATE VIEW para nuestro pipeline y se pasa tal cual a sqlglot (vía uv).
// Compara, por columna de salida, el CONJUNTO de nombres de columna fuente.
//
// Requisitos: binario Release compilado; `uv` en PATH (no instala nada permanente).
// Uso: node compare.mjs [ruta_al_TSqlParser.dll]
import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync, writeFileSync, mkdtempSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const dll = process.argv[2] || join(root, 'src', 'TSqlParser', 'bin', 'Release', 'net10.0', 'TSqlParser.dll');
if (!existsSync(dll)) { console.error('Falta el DLL:', dll); process.exit(2); }

const lastCol = (s) => s.split('.').pop().toLowerCase();           // "dbo.t.a" -> "a"
const tmp = mkdtempSync(join(tmpdir(), 'sgo-'));

// Lineage de NUESTRO motor para un SELECT: lo envolvemos como vista y leemos DERIVES_FROM.
function ourLineage(selectSql, name) {
  const viewSql = `CREATE VIEW dbo.v_${name} AS ${selectSql}`;
  const sqlFile = join(tmp, `${name}.sql`), inp = join(tmp, 'in.json'), gph = join(tmp, 'g.json');
  writeFileSync(sqlFile, viewSql, 'utf8');
  execFileSync('dotnet', [dll, 'from-sql', 'TestDB', inp, sqlFile], { stdio: 'ignore' });
  execFileSync('dotnet', [dll, inp, gph, '--columns'], { stdio: 'ignore' });
  const g = JSON.parse(readFileSync(gph, 'utf8').replace(/^﻿/, ''));
  const N = Object.fromEntries(g.nodes.map(n => [n.id, n]));
  const out = {};
  for (const r of g.relationships.filter(r => r.type === 'DERIVES_FROM')) {
    const o = N[r.source].properties.name.toLowerCase();
    (out[o] ||= new Set()).add(N[r.target].properties.name.toLowerCase());
  }
  return out;
}

function sqlglotLineage(file) {
  const raw = execFileSync('uv', ['run', '--quiet', '--with', 'sqlglot', 'python', join(here, 'sqlglot_lineage.py'), file], { encoding: 'utf8' });
  const j = JSON.parse(raw);
  const out = {};
  for (const [col, srcs] of Object.entries(j)) {
    if (col === '_error') return { _error: srcs };
    out[col.toLowerCase()] = new Set(srcs.map(lastCol));
  }
  return out;
}

const files = readdirSync(join(here, 'cases')).filter(f => f.endsWith('.sql')).sort();
let diffs = 0;
for (const f of files) {
  const name = basename(f, '.sql').replace(/[^a-z0-9]/gi, '_');
  const sel = readFileSync(join(here, 'cases', f), 'utf8').trim();
  const ours = ourLineage(sel, name);
  const sg = sqlglotLineage(join(here, 'cases', f));
  console.log(`\n=== ${f} ===`);
  if (sg._error) { console.log('  sqlglot ERROR:', sg._error); continue; }
  const cols = [...new Set([...Object.keys(ours), ...Object.keys(sg)])];
  for (const c of cols) {
    const o = ours[c] || new Set(), s = sg[c] || new Set();
    const missing = [...s].filter(x => !o.has(x));   // sqlglot lo ve, nosotros no
    const extra = [...o].filter(x => !s.has(x));      // nosotros sí, sqlglot no
    const mark = missing.length === 0 ? 'OK  ' : 'DIFF';
    if (missing.length) diffs++;
    console.log(`  ${mark} ${c.padEnd(16)} nuestro={${[...o].sort()}} sqlglot={${[...s].sort()}}` +
      (missing.length ? `  FALTAN(vs sqlglot)=[${missing}]` : '') + (extra.length ? `  extra=[${extra}]` : ''));
  }
}
console.log(`\n${diffs === 0 ? 'Sin huecos vs sqlglot' : diffs + ' columna(s) donde sqlglot ve fuentes que nosotros no'}`);
