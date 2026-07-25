// Fresh README screenshots from a REAL database (WideWorldImporters).
// Uses the dashboard's own theme/styles. Run: node shots-readme.js
const path = require('path');
const { chromium } = require('playwright');

const INDEX = path.resolve(__dirname, '..', 'index.html');
const GRAPH = path.resolve(__dirname, '..', '..', 'out', 'graph_full.json'); // WWI
const OUT   = path.resolve(__dirname, '..', '..', 'docs');

async function waitMermaid(page) {
  await page.waitForFunction(() => {
    const b = document.querySelectorAll('.mermaid, .mermaid-wrap');
    return !b.length || [...b].every(x => x.querySelector('svg'));
  }, { timeout: 12000 }).catch(() => {});
  await page.waitForTimeout(400);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  const errors = [];
  page.on('pageerror', e => errors.push(String(e)));

  await page.goto('file://' + INDEX.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  await (await page.$('#file')).setInputFiles(GRAPH);
  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 20000 });
  await page.waitForTimeout(800);
  console.log('Subtitle:', await page.evaluate(() => document.querySelector('#subtitle')?.textContent));

  // 1) Overview
  await page.screenshot({ path: path.join(OUT, 'readme-overview.png'), fullPage: true });
  console.log('✓ readme-overview.png');

  // 2) Impact view — resolve the real sidebar name for the richest proc (34 dynamic EXEC)
  const target = await page.evaluate(() => {
    const items = [...document.querySelectorAll('.item')].map(el => el.dataset.n).filter(Boolean);
    return items.find(n => n.includes('DeactivateTemporalTablesBeforeDataLoad'))
        || items.find(n => n.includes('DeactivateTemporalTables'))
        || items[0];
  });
  console.log('Impact target:', target);
  await page.evaluate(n => SD.app.openObject(n), target);
  await waitMermaid(page);
  await page.screenshot({ path: path.join(OUT, 'readme-impact.png'), fullPage: true });
  console.log('✓ readme-impact.png');

  // 3) Risks
  await page.evaluate(() => SD.app.openRisks());
  await page.waitForTimeout(700);
  await page.screenshot({ path: path.join(OUT, 'readme-risks.png'), fullPage: true });
  console.log('✓ readme-risks.png');

  console.log('Errors:', errors);
  await browser.close();
})();
