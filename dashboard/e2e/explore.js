const path = require('path');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const indexPath = path.resolve(__dirname, '..', 'index.html');
  const samplePath = path.resolve(__dirname, '..', '..', 'samples', 'from-sql-demo', 'graph.json');

  await page.goto('file://' + indexPath.replaceAll('\\', '/'));
  await page.waitForSelector('#drop');
  await (await page.$('#file')).setInputFiles(samplePath);
  await page.waitForFunction(() => document.body.classList.contains('loaded'));

  // click on the object in the sidebar
  await page.click('text=Sales.usp_UpdateCustomerEmail');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'screenshot-object.png', fullPage: true });
  console.log('done');
  await browser.close();
})();
