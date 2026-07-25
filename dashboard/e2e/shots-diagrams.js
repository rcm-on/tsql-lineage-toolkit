// Diagram-only screenshots for the README: impact chain (by levels/depth) and
// business control-flow. WWI. Run: node shots-diagrams.js
const path = require('path');
const { chromium } = require('playwright');

const INDEX = path.resolve(__dirname, '..', 'index.html');
const GRAPH = path.resolve(__dirname, '..', '..', 'out', 'graph_full.json');
const OUT   = path.resolve(__dirname, '..', '..', 'docs');

async function waitMermaid(page) {
  await page.waitForFunction(() => {
    const b = document.querySelectorAll('.mermaid, .mermaid-wrap');
    return b.length && [...b].every(x => x.querySelector('svg'));
  }, { timeout: 12000 }).catch(() => {});
  await page.waitForTimeout(500);
}

// Screenshot the mermaid diagram that follows a given <h3> heading.
async function shotDiagram(page, headingText, out) {
  const handle = await page.evaluateHandle((txt) => {
    const h = [...document.querySelectorAll('h3')].find(x => x.textContent.includes(txt));
    if (!h) return null;
    let el = h.nextElementSibling;
    for (let i = 0; i < 4 && el; i++, el = el.nextElementSibling) {
      const svg = (el.matches && el.matches('svg')) ? el
                : (el.querySelector ? el.querySelector('svg') : null);
      if (svg) return svg;                      // tight, content-sized bounds
      if (el.matches && el.matches('.mermaid-wrap, .mermaid')) return el;
    }
    return null;
  }, headingText);
  const el = handle.asElement();
  if (!el) { console.log('✗ no diagram for', headingText); return false; }
  await el.scrollIntoViewIfNeeded();
  await page.waitForTimeout(300);
  await el.screenshot({ path: out });
  console.log('✓', path.basename(out));
  return true;
}

async function openByLabel(page, needle) {
  const name = await page.evaluate((n) => {
    const items = [...document.querySelectorAll('.item')].map(el => el.dataset.n).filter(Boolean);
    return items.find(x => x.includes(n)) || null;
  }, needle);
  if (!name) { console.log('✗ not found:', needle); return; }
  await page.evaluate(n => SD.app.openObject(n), name);
  await waitMermaid(page);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 2 });
  await page.goto('file://' + INDEX.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  await (await page.$('#file')).setInputFiles(GRAPH);
  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 20000 });
  await page.waitForTimeout(800);

  // Impact chain by levels + depth
  await page.evaluate(() => SD.app.setImpactDepth(5));
  await openByLabel(page, 'Configuration_ConfigureForEnterpriseEdition');
  await shotDiagram(page, 'Cadena de impacto', path.join(OUT, 'readme-impact-chain.png'));

  // Business control flow (steps) — try several, pick the one with clear branches
  for (const [needle, file] of [
    ['InvoiceCustomerOrders', 'flow-invoice.png'],
    ['Configuration_ApplyAuditing', 'flow-auditing.png'],
    ['RecordVehicleTemperature', 'flow-vehicle.png'],
  ]) {
    await openByLabel(page, needle);
    await shotDiagram(page, 'Flujograma de control', path.join(OUT, file));
  }

  await browser.close();
})();
