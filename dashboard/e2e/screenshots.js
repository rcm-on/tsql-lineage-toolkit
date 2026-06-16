// Generates README screenshots (docs/).
// Run: node screenshots.js
const path = require('path');
const fs   = require('fs');
const { chromium } = require('playwright');

const OUT          = path.resolve(__dirname, '..', '..', 'docs');
const INDEX        = path.resolve(__dirname, '..', 'index.html');
const DEMO_GRAPH   = path.resolve(__dirname, '..', '..', 'samples', 'from-sql-demo', 'graph.json');
const ENRICHED     = path.resolve(__dirname, '..', '..', '..', 'eval', 'eval_graph_enriched.json');

async function loadGraph(page, graphPath) {
  await page.goto('file://' + INDEX.replace(/\\/g, '/'));
  await page.waitForSelector('#drop');
  await (await page.$('#file')).setInputFiles(graphPath);
  await page.waitForFunction(() => document.body.classList.contains('loaded'), { timeout: 15000 });
  await page.waitForTimeout(600);
}

// Navigate to a named entity using the app's internal API
async function nav(page, name) {
  await page.evaluate(n => SD.app.openObject(n), name);
  await page.waitForTimeout(900);
}

// Wait for all Mermaid SVGs to render
async function waitMermaid(page) {
  await page.waitForFunction(() => {
    const blocks = document.querySelectorAll('.mermaid, .mermaid-wrap');
    if (!blocks.length) return true;  // no mermaid on page = ok
    return [...blocks].every(b => b.querySelector('svg'));
  }, { timeout: 8000 }).catch(() => {});
  await page.waitForTimeout(300);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

  // ── DEMO GRAPH ────────────────────────────────────────────────────────────────
  await loadGraph(page, DEMO_GRAPH);

  // 1. Overview — general stats, complexity chart, hotspot tables
  await page.screenshot({ path: path.join(OUT, 'dashboard-overview.png'), fullPage: true });
  console.log('✓ dashboard-overview.png');

  // 2. Object detail — metrics, risks, references, parameters, quick-graph
  await nav(page, 'Sales.usp_UpdateCustomerEmail');
  await waitMermaid(page);
  await page.screenshot({ path: path.join(OUT, 'dashboard-object.png'), fullPage: true });
  console.log('✓ dashboard-object.png');

  // 3. Placeholder workflow screenshot — replaced below with enriched graph complex proc
  await page.screenshot({ path: path.join(OUT, 'dashboard-workflow.png'), fullPage: true });
  console.log('✓ dashboard-workflow.png (placeholder — will be replaced with enriched graph)');

  // 4. Table with columns + quick lineage graph
  await nav(page, 'dbo.Customers');
  await waitMermaid(page);
  await page.screenshot({ path: path.join(OUT, 'dashboard-table.png'), fullPage: true });
  console.log('✓ dashboard-table.png');

  // 5. Risks panel
  await page.evaluate(() => SD.app.openRisks());
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(OUT, 'dashboard-risks.png'), fullPage: true });
  console.log('✓ dashboard-risks.png');

  // ── ENRICHED GRAPH (execution plan + columns) ─────────────────────────────────
  if (fs.existsSync(ENRICHED)) {
    await loadGraph(page, ENRICHED);

    // 6a. Workflow — use complex proc with IF/WHILE branching (cc=3)
    await nav(page, 'dbo.spExtraerVentasDiarias');
    await waitMermaid(page);
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(OUT, 'dashboard-workflow.png'), fullPage: true });
    console.log('✓ dashboard-workflow.png (replaced with enriched complex proc)');

    // 6b. Proc with execution plan section
    await nav(page, 'dbo.spCargarClientes');
    await waitMermaid(page);
    await page.screenshot({ path: path.join(OUT, 'dashboard-execution.png'), fullPage: true });
    console.log('✓ dashboard-execution.png');

    // Scroll to "Ejecución real" section and crop for a focused shot
    const planHeader = await page.$('text=Ejecución real');
    if (planHeader) {
      await planHeader.scrollIntoViewIfNeeded();
      await page.waitForTimeout(400);
      const box = await planHeader.boundingBox();
      if (box) {
        await page.screenshot({
          path: path.join(OUT, 'dashboard-execution-zoom.png'),
          clip: { x: 330, y: Math.max(0, box.y - 16), width: 1110, height: 320 },
        });
        console.log('✓ dashboard-execution-zoom.png');
      }
    }

    // 7. Table with columns + FK (enriched graph has --columns data)
    await nav(page, 'dbo.Clientes');
    await waitMermaid(page);
    await page.screenshot({ path: path.join(OUT, 'dashboard-columns.png'), fullPage: true });
    console.log('✓ dashboard-columns.png');
  } else {
    console.log('⚠ eval_graph_enriched.json not found — skipping execution plan shots');
    console.log('  Generate it with: dotnet run -- enrich-from-plans ...');
  }

  console.log('\nAll screenshots saved to', OUT);
  await browser.close();
})();
