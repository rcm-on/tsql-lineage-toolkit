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

  // The layout scrolls internally (the content pane, not window/body/html),
  // so `window.scrollTo` never touches the container that actually matters.
  // Reset every scrollable ancestor's scrollTop to 0 directly.
  const resetScroll = () => el.evaluate((node) => {
    let e = node.parentElement;
    while (e) {
      if (e.scrollHeight > e.clientHeight + 2) e.scrollTop = 0;
      e = e.parentElement;
    }
  });
  await resetScroll();
  await page.waitForTimeout(200);

  // Chromium's element screenshot goes blank outside the current viewport for
  // elements taller than it (e.g. a long flowchart with 15+ ranks): only the
  // scrolled-into-view slice gets painted, the rest comes out empty. Grow the
  // viewport to fully contain the element before capturing, then restore it —
  // no scrolling/tiling needed, so the whole element gets painted. Must reset
  // scroll again afterward: resizing a taller viewport can itself trigger the
  // content pane to re-scroll to keep the previous focus point in view.
  const box = await el.boundingBox();
  const original = page.viewportSize();
  const needsResize = box && (box.y + box.height + 50) > original.height;
  if (needsResize) {
    await page.setViewportSize({ width: original.width, height: Math.ceil(box.y + box.height + 50) });
    await page.waitForTimeout(200);
    await resetScroll();
    await page.waitForTimeout(300);
  }
  await el.screenshot({ path: out });
  if (needsResize) await page.setViewportSize(original);
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
