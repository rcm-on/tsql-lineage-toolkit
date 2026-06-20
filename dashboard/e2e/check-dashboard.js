// Smoke test: opens the dashboard (file://) and uploads a sample workflows.json,
// then screenshots the result. Run with: node check-dashboard.js
const path = require('path');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();

  const indexPath = path.resolve(__dirname, '..', 'index.html');
  const samplePath = path.resolve(__dirname, '..', '..', 'samples', 'from-sql-demo', 'graph.json');

  const errors = [];
  const dialogs = [];
  page.on('pageerror', e => errors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('dialog', async d => { dialogs.push(d.message()); await d.dismiss(); });

  await page.goto('file://' + indexPath.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');

  const fileInput = await page.$('#file');
  await fileInput.setInputFiles(samplePath);

  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 10000 }).catch(() => {});
  await page.waitForTimeout(1000);

  // Open an object with a CALLS edge (Sales.usp_UpdateCustomerEmail -> dbo.usp_GetCustomerEmail
  // in the sample graph) so the new "Cadena de impacto" section renders its Mermaid diagram.
  await page.evaluate(() => SD.app.openObject('Sales.usp_UpdateCustomerEmail'));
  await page.waitForTimeout(1000);
  const impactHeading = await page.evaluate(() => Array.from(document.querySelectorAll('h3')).some(h => h.textContent.includes('Cadena de impacto')));
  console.log('Impact chain section present:', impactHeading);

  await page.screenshot({ path: path.join(__dirname, 'screenshot.png'), fullPage: true });

  console.log('Screenshot saved to', path.join(__dirname, 'screenshot.png'));
  console.log('Loaded:', await page.evaluate(() => document.body.classList.contains('loaded')));
  console.log('Subtitle:', await page.evaluate(() => document.querySelector('#subtitle')?.textContent));
  console.log('Dialogs:', dialogs);
  console.log('Errors:', errors);

  await browser.close();
})();
