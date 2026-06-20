// Compara, para los 47 objetos de WideWorldImporters:
//  1) nº de pasos declarado en graph_full.json (HAS_STEP) [fuente]
//  2) nº de pasos declarado en el nodestore (owned.steps.length) [misma fuente, otro formato]
//  3) nº de pasos que el dashboard renderiza de verdad en el árbol de texto (DOM real)
// con impactDepth=0 para que el árbol no se contamine con expansión recursiva de EXEC.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const strip = s => s.replace(/^﻿/, '');
const outDir = path.resolve(__dirname, '..', '..', 'out');

const g = JSON.parse(strip(fs.readFileSync(path.join(outDir, 'graph_full.json'), 'utf8')));
const hasStepCount = {};
for (const r of g.relationships) {
  if (r.type === 'HAS_STEP') hasStepCount[r.source] = (hasStepCount[r.source] || 0) + 1;
}

const nodestoreCount = {};
const objectsDir = path.join(outDir, 'graph_full.nodes', 'objects');
for (const slug of fs.readdirSync(objectsDir)) {
  const objPath = path.join(objectsDir, slug, 'object.json');
  if (!fs.existsSync(objPath)) continue;
  const obj = JSON.parse(strip(fs.readFileSync(objPath, 'utf8')));
  nodestoreCount[obj.id] = obj.owned.steps.length;
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const indexPath = path.resolve(__dirname, '..', 'index.html');
  const errors = [];
  page.on('pageerror', e => errors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });

  await page.goto('file://' + indexPath.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  const fileInput = await page.$('#file');
  await fileInput.setInputFiles(path.join(outDir, 'graph_full.json'));
  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(800);

  const names = await page.evaluate(() => Array.from(document.querySelectorAll('.item.object')).map(e => e.dataset.n));
  console.log('objetos en el dashboard:', names.length);

  const mismatches = [];
  for (const name of names) {
    await page.evaluate((n) => SD.app.openObject(n), name);
    await page.waitForTimeout(80);
    // solo pasos propios del objeto: excluye los que vienen de una expansión
    // recursiva de EXEC anidada (envuelta en <details class="tree-fold sub">)
    const domSteps = await page.evaluate(() =>
      Array.from(document.querySelectorAll('.tree .step')).filter(el => !el.closest('.tree-fold.sub')).length);
    const fullId = 'WideWorldImporters::' + name;
    const jsonCount = hasStepCount[fullId] ?? -1;
    const nsCount = nodestoreCount[fullId] ?? -1;
    if (domSteps !== jsonCount || jsonCount !== nsCount) {
      mismatches.push({ name, jsonCount, nsCount, domSteps });
    }
  }

  console.log('Errores JS durante el barrido:', errors);
  console.log('Discrepancias (json vs nodestore vs DOM):', mismatches.length);
  if (mismatches.length) console.log(mismatches);
  else console.log('Los 47 objetos coinciden exactamente: graph_full.json == nodestore == render del dashboard.');

  await browser.close();
})();
