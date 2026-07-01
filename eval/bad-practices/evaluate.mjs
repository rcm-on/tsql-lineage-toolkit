#!/usr/bin/env node
// Comparador de evaluación del analizador de malas prácticas.
//
// Carga el graph_full.json generado por el toolkit, ejecuta EXACTAMENTE las mismas
// reglas que usa el dashboard (dashboard/src/{naturalize,shape,risks}.js, sin
// duplicar lógica) y contrasta los hallazgos reales con el ground-truth
// (expected-findings.json). Reporta:
//   FALTAN  - hallazgo esperado que el analizador NO produjo  (regresión / falso negativo)
//   SOBRAN  - hallazgo producido que NO se esperaba            (ruido / falso positivo)
//   SEV/CAT - severidad o categoría distinta de la esperada
// Sale con código != 0 si hay cualquier discrepancia (apto como gate de CI).
//
// Uso:
//   node evaluate.mjs [graph_full.json] [expected-findings.json] [--json]
// Por defecto: ./graph_full.json y ./expected-findings.json junto a este script.
//
//   --json  emite un informe estructurado (máquina-legible) en stdout en vez del
//           texto coloreado. Pensado para que un AGENTE o un job de CI lo
//           interprete como salida de prueba: { pass, summary, components[] }.
//           El exit code (0 = pasa, 1 = discrepancias) es idéntico en ambos modos.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const asJson = args.includes('--json');
const positional = args.filter(a => !a.startsWith('--'));
const graphPath = positional[0] || path.join(here, 'graph_full.json');
const expectedPath = positional[1] || path.join(here, 'expected-findings.json');
const dashSrc = path.resolve(here, '..', '..', 'dashboard', 'src');

// ── Cargar las reglas del dashboard en un "window" simulado ────────────────────
globalThis.window = globalThis.window || {};
const loadDash = f => {
  const code = fs.readFileSync(path.join(dashSrc, f), 'utf8');
  (0, eval)(code); // indirect eval: corre en ámbito global, resuelve `window`
};
for (const f of ['naturalize.js', 'shape.js', 'risks.js']) loadDash(f);
const SD = globalThis.window.SD;
if (!SD || !SD.shape || !SD.risks) {
  console.error('No se pudieron cargar las reglas del dashboard (SD.shape / SD.risks).');
  process.exit(2);
}

// ── Ejecutar análisis real ─────────────────────────────────────────────────────
const readJson = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, '')); // tolera BOM
const graph = readJson(graphPath);
const DATA = SD.shape(graph);
const { findings } = SD.risks.analyze(DATA);

// Índice de hallazgos reales por componente -> Map(rule -> {sev,cat})
const actual = new Map();
for (const f of findings) {
  if (!actual.has(f.component)) actual.set(f.component, new Map());
  actual.get(f.component).set(f.rule, { sev: f.sev, cat: f.cat });
}

// ── Cargar ground-truth ──────────────────────────────────────────────────────
const spec = readJson(expectedPath);

// ── Comparar → informe estructurado ──────────────────────────────────────────
// Un único modelo (report) del que luego se renderiza texto humano o JSON. Cada
// check lleva status: OK | MISSING | UNEXPECTED | SEV_CAT, con expected/actual.
let missing = 0, extra = 0, mismatch = 0, okCount = 0;
const seenComponents = new Set();
const components = [];

for (const c of spec.components) {
  seenComponents.add(c.component);
  const got = actual.get(c.component) || new Map();
  const want = new Map((c.expected || []).map(e => [e.rule, e]));
  const checks = [];

  for (const [rule, e] of want) {
    const g = got.get(rule);
    if (!g) { checks.push({ rule, status: 'MISSING', expected: { sev: e.sev, cat: e.cat }, actual: null }); missing++; }
    else if ((e.sev && e.sev !== g.sev) || (e.cat && e.cat !== g.cat)) {
      checks.push({ rule, status: 'SEV_CAT', expected: { sev: e.sev, cat: e.cat }, actual: g }); mismatch++; okCount++;
    } else {
      checks.push({ rule, status: 'OK', expected: { sev: e.sev, cat: e.cat }, actual: g }); okCount++;
    }
  }
  for (const [rule, g] of got)
    if (!want.has(rule)) { checks.push({ rule, status: 'UNEXPECTED', expected: null, actual: g }); extra++; }

  const isControl = (c.expected || []).length === 0 && got.size === 0;
  components.push({ component: c.component, note: c.note || '', inGroundTruth: true, control: isControl, checks });
}

// Componentes con hallazgos pero ausentes del ground-truth (corpus desincronizado).
for (const [component, rules] of actual) {
  if (seenComponents.has(component)) continue;
  const checks = [];
  for (const [rule, g] of rules) { checks.push({ rule, status: 'UNEXPECTED', expected: null, actual: g }); extra++; }
  components.push({ component, note: 'no está en el ground-truth', inGroundTruth: false, control: false, checks });
}

const fail = missing + extra + mismatch;
const report = {
  pass: fail === 0,
  graph: graphPath,
  expected: expectedPath,
  summary: { ok: okCount, missing, unexpected: extra, sevCat: mismatch, total: okCount + missing + extra },
  components,
};

// ── Render ───────────────────────────────────────────────────────────────────
if (asJson) {
  process.stdout.write(JSON.stringify(report, null, 2) + '\n');
  process.exit(fail === 0 ? 0 : 1);
}

const C = { reset: '\x1b[0m', red: '\x1b[31m', green: '\x1b[32m', yellow: '\x1b[33m', cyan: '\x1b[36m', dim: '\x1b[2m' };
const COLOR = { OK: C.green, MISSING: C.red, UNEXPECTED: C.red, SEV_CAT: C.yellow };
const LABEL = { OK: 'OK     ', MISSING: 'FALTA  ', UNEXPECTED: 'SOBRA  ', SEV_CAT: 'SEV/CAT' };
console.log(`\n${C.cyan}=== Evaluación del analizador de malas prácticas ===${C.reset}`);
console.log(`${C.dim}grafo:    ${graphPath}`);
console.log(`esperado: ${expectedPath}${C.reset}\n`);
for (const c of components) {
  console.log(`${C.cyan}${c.component}${C.reset}${c.note ? `  ${C.dim}// ${c.note}${C.reset}` : ''}`);
  if (c.control) console.log(`  ${C.green}OK      ${C.reset} sin hallazgos (control)`);
  for (const ck of c.checks) {
    const det = ck.status === 'MISSING' ? `(esperado ${ck.expected.sev}/${ck.expected.cat})`
      : ck.status === 'SEV_CAT' ? `: esperado ${ck.expected.sev}/${ck.expected.cat}, obtenido ${ck.actual.sev}/${ck.actual.cat}`
      : ck.status === 'UNEXPECTED' ? `(${ck.actual.sev}/${ck.actual.cat}) -- no esperado`
      : `(${ck.actual.sev}/${ck.actual.cat})`;
    console.log(`  ${COLOR[ck.status]}${LABEL[ck.status]}${C.reset} ${ck.rule} ${det}`);
  }
  console.log('');
}
const summary = `OK=${okCount}  FALTAN=${missing}  SOBRAN=${extra}  SEV/CAT=${mismatch}`;
console.log(`${C.cyan}=== Resumen ===${C.reset}`);
console.log(fail === 0 ? `${C.green}PASA  ${summary}${C.reset}` : `${C.red}FALLA ${summary}${C.reset}`);
process.exit(fail === 0 ? 0 : 1);
