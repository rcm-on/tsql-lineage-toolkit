#!/usr/bin/env node
// DEPRECADO (2026-07-04, paso 3-extensión de docs/task-gates-dotnet.md): este
// script sigue funcionando tal cual (no se ha tocado su lógica) pero el gate
// real ahora es tests/TSqlParser.Tests/AuditorChallengeGateTests.cs (dotnet
// test, in-process, misma comprobación exacta contra un nodestore WWI
// regenerado con [Trait("Category","Oracle")]). Se conserva sin borrar como
// guardia de paridad JS↔C# (correrlo a mano contra un nodestore regenerado
// detecta divergencia entre ambos motores) hasta que nada dependa de él.
//
// Regresión ejecutable del ejercicio de auditoría cruzada (docs/auditor-challenge.md).
// Las conclusiones de docs/claude-audit-report.md y docs/gemini-audit-report.md sobre
// DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad (WWI) eran afirmaciones en prosa
// con cifras citadas a mano; este script las re-deriva del NodeStore real cada vez que se
// ejecuta, para que un cambio futuro (Tarea A/AdventureWorks, fix del gap 5.2 de
// docs/extraction-gaps.md, regeneración de out/) las rompa de forma audible en vez de dejar
// los informes desactualizados en silencio.
//
// Uso:  node eval/auditor-challenge/verify.mjs [ruta-al-nodestore]
//       (por defecto out/graph_full.nodes, el nodestore de producción de WWI)
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const nodestore = process.argv[2] || join(root, 'out', 'graph_full.nodes');
const read = (p) => JSON.parse(readFileSync(p, 'utf8').replace(/^﻿/, ''));

if (!existsSync(join(nodestore, 'model.json'))) {
  console.error('No encuentro', nodestore, '- genera el nodestore primero (--nodestore en el pipeline).');
  process.exit(2);
}

let fails = 0;
function check(label, ok, detail) {
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${label}${detail ? '  — ' + detail : ''}`);
  if (!ok) fails++;
}

const model = read(join(nodestore, 'model.json'));
const PROC_NAME = 'DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad';
const PROC_ID = `WideWorldImporters::${PROC_NAME}`;
const proc = model.nodes.find((n) => n.id === PROC_ID);

if (!proc) {
  console.error(`No encuentro ${PROC_ID} en ${nodestore}/model.json — ¿es el nodestore de WWI?`);
  process.exit(2);
}

// ── Hotspot #3 del informe: complejidad + punto ciego de SQL dinámico ──────
check('cyclomatic_complexity == 19 (snapshot WWI)', proc.cyclomatic_complexity === 19, `real=${proc.cyclomatic_complexity}`);
check(
  'unresolved_dynamic_sql_steps == 0 (gaps 5.1 QUOTENAME y 5.2 NCHAR/CASE/COALESCE cerrados)',
  proc.unresolved_dynamic_sql_steps === 0,
  `real=${proc.unresolved_dynamic_sql_steps}/${proc.dynamic_sql_steps} (era 34/34 sin fix, 17/34 tras solo QUOTENAME)`
);

// ── Las 17 tablas escritas (ALTER, vía nav.json) — "ninguna tabla 18ª oculta" ──
const EXPECTED_WRITTEN_TABLES = new Set([
  'application.cities', 'application.countries', 'application.deliverymethods',
  'application.paymentmethods', 'application.people', 'application.stateprovinces',
  'application.transactiontypes', 'purchasing.suppliercategories', 'purchasing.suppliers',
  'sales.buyinggroups', 'sales.customercategories', 'sales.customers',
  'warehouse.coldroomtemperatures', 'warehouse.colors', 'warehouse.packagetypes',
  'warehouse.stockgroups', 'warehouse.stockitems',
]);

function slug(objId) {
  return objId.replace(/^WideWorldImporters::/, 'WideWorldImporters_');
}

const nav = read(join(nodestore, 'objects', slug(PROC_ID), 'nav.json'));
const writtenTables = new Set(
  nav.edges_out
    .filter((e) => e.type === 'WRITES_TO')
    .map((e) => e.to.replace(/^WideWorldImporters:table:/, ''))
);
const missing = [...EXPECTED_WRITTEN_TABLES].filter((t) => !writtenTables.has(t));
const extra = [...writtenTables].filter((t) => !EXPECTED_WRITTEN_TABLES.has(t));
check(
  `WRITES_TO == exactamente las 17 tablas conocidas (sin tabla 18ª oculta)`,
  missing.length === 0 && extra.length === 0,
  `n=${writtenTables.size}${missing.length ? ' FALTAN=' + missing.join(',') : ''}${extra.length ? ' SOBRAN=' + extra.join(',') : ''}`
);

// ── Impacto en lineage_path.json de las 3 vistas de Website ────────────────
function lineageCoverage(viewSlug) {
  const path = join(nodestore, 'objects', viewSlug, 'lineage_path.json');
  if (!existsSync(path)) return null;
  const lp = read(path);
  const cols = Object.entries(lp);
  const impacted = cols.filter(([, info]) =>
    info.roots.some((root) => {
      const table = root.split('.').slice(0, 2).join('.').toLowerCase();
      return writtenTables.has(table);
    })
  );
  return { total: cols.length, impacted: impacted.length };
}

const customers = lineageCoverage('WideWorldImporters_Website.Customers');
check('Website.Customers: 14/14 columnas impactadas', customers && customers.impacted === 14 && customers.total === 14, customers ? `${customers.impacted}/${customers.total}` : 'sin lineage_path.json');

const suppliers = lineageCoverage('WideWorldImporters_Website.Suppliers');
check('Website.Suppliers: 12/12 columnas impactadas', suppliers && suppliers.impacted === 12 && suppliers.total === 12, suppliers ? `${suppliers.impacted}/${suppliers.total}` : 'sin lineage_path.json');

const vehicleTemps = lineageCoverage('WideWorldImporters_Website.VehicleTemperatures');
check(
  'Website.VehicleTemperatures: 0/6 columnas impactadas (raíz distinta, vista NO afectada — caso negativo)',
  vehicleTemps && vehicleTemps.impacted === 0 && vehicleTemps.total === 6,
  vehicleTemps ? `${vehicleTemps.impacted}/${vehicleTemps.total}` : 'sin lineage_path.json'
);

console.log(`\n${fails === 0 ? 'TODOS OK' : fails + ' CHECK(S) FALLIDO(S)'}`);
process.exit(fails > 0 ? 1 : 0);
