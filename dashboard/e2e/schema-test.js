const path = require('path');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', e => errors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });

  const INDEX = path.resolve(__dirname, '..', 'index.html');
  const GRAPH = path.resolve(__dirname, '..', '..', '..', 'eval', 'eval_graph_enriched.json');
  const OUT   = path.resolve(__dirname, '..', '..', 'docs');

  await page.goto('file://' + INDEX.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  const fileInput = await page.$('#file');
  await fileInput.setInputFiles(GRAPH);
  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 15000 });
  await page.waitForTimeout(800);

  // Open Schema ORM (empty state)
  await page.evaluate(() => SD.app.openSchema());
  await page.waitForTimeout(600);
  await page.screenshot({ path: path.join(OUT, 'dashboard-schema-empty.png') });
  console.log('schema-empty.png captured');

  // Add dbo.Clientes then expand its FK neighbours
  await page.evaluate(() => SD.app.schemaAdd('dbo.Clientes'));
  await page.waitForTimeout(1200);
  await page.evaluate(() => SD.app.schemaExpand('dbo.Clientes'));
  await page.waitForTimeout(2000);  // wait for Mermaid to render
  await page.screenshot({ path: path.join(OUT, 'dashboard-schema-orm.png'), fullPage: true });
  console.log('dashboard-schema-orm.png captured');

  if (errors.length) console.error('JS errors:', errors);
  else console.log('No JS errors');
  await browser.close();
})();
