// Generates the screenshots used in the top-level README (docs/).
// Run with: node screenshots.js
const path = require('path');
const { chromium } = require('playwright');

const OUT = path.resolve(__dirname, '..', '..', 'docs');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

  const indexPath = path.resolve(__dirname, '..', 'index.html');
  const samplePath = path.resolve(__dirname, '..', '..', 'samples', 'from-sql-demo', 'graph.json');

  await page.goto('file://' + indexPath.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  await (await page.$('#file')).setInputFiles(samplePath);
  await page.waitForFunction(() => document.body.classList.contains('loaded'));
  await page.waitForTimeout(500);

  // 1. Overview
  await page.screenshot({ path: path.join(OUT, 'dashboard-overview.png'), fullPage: true });

  // 2. Object view (lineage de un procedimiento concreto, con su flow chart)
  await page.click('text=Sales.usp_UpdateCustomerEmail');
  await page.waitForTimeout(800);
  await page.screenshot({ path: path.join(OUT, 'dashboard-object.png'), fullPage: true });

  // 3. Table view (lineage de una tabla: columnas, FKs, lecturas/escrituras)
  await page.click('text=dbo.customers');
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(OUT, 'dashboard-table.png'), fullPage: true });

  // 4. Risks view (hallazgos detectados en todo el grafo)
  await page.evaluate(() => SD.app.openRisks());
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(OUT, 'dashboard-risks.png'), fullPage: true });

  console.log('Screenshots saved to', OUT);
  await browser.close();
})();
