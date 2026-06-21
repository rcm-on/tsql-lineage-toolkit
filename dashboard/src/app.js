// Orquestador: carga (upload/drag) del workflows_full.json, estado, routing y
// cableado de los componentes. Expuesto como SD.app.
(function (SD) {
  const $ = s => document.querySelector(s);
  let DATA = null, current = null, kindFilter = 'all', dynOnly = false, impactDepth = 3;
  let schemaPinned = new Set();  // tables currently pinned in the Schema Explorer

  // Shared nav bar: always includes Overview, Risks and Schema ORM buttons.
  function navBar(active) {
    const btn = (id, label, onclick) =>
      `<button class="${active === id ? 'on' : ''}" onclick="${onclick}">${label}</button>`;
    return `<div class="nav">
      ${btn('overview', '◉ Resumen general', 'SD.app.openOverview()')}
      ${btn('risks',    '⚠ Riesgos',          'SD.app.openRisks()')}
      ${btn('schema',   '📐 Esquema ORM',      'SD.app.openSchema()')}
    </div>`;
  }

  // ¿Pasa la entidad el filtro de tipo activo? 'table' = solo tablas reales;
  // 'temp' = solo tablas temporales (#nombre); el resto por kind.
  function kindOk(e) {
    if (!kindFilter || kindFilter === 'all') return true;
    if (kindFilter === 'table') return e.kind === 'table' && !e.temp;
    if (kindFilter === 'temp') return e.kind === 'table' && e.temp;
    return e.kind === kindFilter;
  }

  function renderFilter() {
    const tcount = DATA.entities.filter(e => e.kind === 'table' && !e.temp).length;
    const tmp = DATA.entities.filter(e => e.kind === 'table' && e.temp).length;
    const counts = { all: DATA.entities.length, object: DATA.objects.length, table: tcount, temp: tmp };
    const defs = [['all', 'Todos'], ['object', 'Objetos'], ['table', 'Tablas']];
    if (tmp) defs.push(['temp', 'Temp']);
    const chips = defs
      .map(([k, lbl]) => `<span class="fchip ${kindFilter === k && !dynOnly ? 'on' : ''}" onclick="SD.app.setFilter('${k}')">${lbl} <b>${counts[k]}</b></span>`).join('');
    $('#filter').innerHTML = chips + (dynOnly ? `<span class="fchip on" onclick="SD.app.setFilter('object')">SQL dinámico ✕</span>` : '');
  }

  function renderSidebar(filter) {
    let ents = DATA.entities.filter(kindOk);
    if (dynOnly) ents = ents.filter(e => e.kind === 'object' && e.dyn > 0);
    const f = (filter || '').toLowerCase();
    const rows = ents.filter(e => e.name.toLowerCase().includes(f)).map(e => {
      const badges = e.kind === 'table'
        ? (e.temp ? `<span class="b tmp">⌗ ${e.cols} tmp</span>` : `<span class="b tbl">▦ ${e.cols}</span>`)
        : [e.complexity > 1 ? `<span class="b cc">cc${e.complexity}</span>` : '', e.dyn > 0 ? `<span class="b dyn">dyn${e.dyn}</span>` : '', e.parseError ? `<span class="b err">err</span>` : ''].join('');
      return `<div class="item ${e.kind}${e.temp ? ' temp' : ''}" data-n="${esc(e.name)}"><span class="nm">${esc(e.name)}</span><span class="badges">${badges}</span></div>`;
    }).join('');
    $('#list').innerHTML = rows || `<div class="muted" style="padding:12px">Sin coincidencias</div>`;
    document.querySelectorAll('.item').forEach(el => el.onclick = () => openObject(el.dataset.n));
    document.querySelectorAll('.item').forEach(el => el.classList.toggle('sel', el.dataset.n === current));
  }

  function setFilter(kind, mode) {
    kindFilter = kind; dynOnly = (mode === 'dyn');
    renderFilter(); renderSidebar($('#search').value);
    $('#list').scrollTop = 0;
  }
  const esc = s => (s == null ? '' : ('' + s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'));

  function openOverview() {
    current = null;
    document.querySelectorAll('.item').forEach(e => e.classList.remove('sel'));
    $('#main').innerHTML = navBar('overview') + SD.components.Overview(DATA);
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
  }

  function openRisks() {
    current = null;
    document.querySelectorAll('.item').forEach(e => e.classList.remove('sel'));
    $('#main').innerHTML = navBar('risks') + '<h2>Riesgos y malas prácticas</h2>' + SD.components.RisksView(DATA);
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
  }

  function openObject(name) {
    const e = DATA.byName[name]; if (!e) return;
    current = name;
    document.querySelectorAll('.item').forEach(el => el.classList.toggle('sel', el.dataset.n === name));
    const view = e.kind === 'table' ? SD.components.TableView(e, DATA, impactDepth) : SD.components.ObjectView(e, DATA, impactDepth);
    $('#main').innerHTML = navBar('') + view;
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
  }

  // Cambia la profundidad de la "Cadena de impacto" (1-5) y re-renderiza la
  // vista actual con el nuevo valor (mismo patrón que schemaAdd -> openSchema()).
  function setImpactDepth(n) {
    impactDepth = +n || 3;
    if (current) openObject(current);
  }

  // ── SCHEMA EXPLORER ──────────────────────────────────────────────────────────

  function openSchema() {
    current = 'schema';
    document.querySelectorAll('.item').forEach(e => e.classList.remove('sel'));
    $('#main').innerHTML = navBar('schema') + SD.components.SchemaView(DATA, schemaPinned);
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main')).then(attachSchemaClicks);
  }

  function schemaAdd(name) {
    if (DATA.byName[name]) { schemaPinned.add(name); openSchema(); }
  }

  function schemaRemove(name) {
    schemaPinned.delete(name); openSchema();
  }

  function schemaExpand(name) {
    const t = DATA.byName[name];
    if (!t || t.kind !== 'table') return;
    [...(t.fkOut || []).map(f => f.table), ...(t.fkIn || []).map(f => f.table)]
      .filter(n => DATA.byName[n] && DATA.byName[n].kind === 'table')
      .forEach(n => schemaPinned.add(n));
    openSchema();
  }

  function schemaClear() { schemaPinned.clear(); openSchema(); }

  // After Mermaid renders the erDiagram SVG, make entity nodes clickable:
  // clicking a table name in the diagram expands its FK neighbours.
  function attachSchemaClicks() {
    const wrap = document.getElementById('schema-er-wrap');
    if (!wrap) return;
    const svg = wrap.querySelector('svg');
    if (!svg) return;
    const safe = name => name.replace(/[^a-zA-Z0-9]/g, '_');
    // Build reverse map: safeName -> realName
    const s2r = {};
    for (const name of schemaPinned) s2r[safe(name)] = name;

    // Mermaid erDiagram renders entity headers as <text> elements inside <g> groups.
    // We walk up from each matching <text> to its nearest <g> ancestor and wire the click.
    const visited = new Set();
    svg.querySelectorAll('text, tspan').forEach(el => {
      const txt = (el.textContent || '').trim();
      const realName = s2r[txt];
      if (!realName) return;
      let g = el.parentElement;
      while (g && g.tagName.toLowerCase() !== 'g') g = g.parentElement;
      if (!g || visited.has(g)) return;
      visited.add(g);
      const t = DATA.byName[realName];
      const neighbors = [
        ...(t ? (t.fkOut || []).map(f => f.table) : []),
        ...(t ? (t.fkIn  || []).map(f => f.table) : []),
      ].filter(n => DATA.byName[n] && DATA.byName[n].kind === 'table' && !schemaPinned.has(n));
      if (!neighbors.length) return;  // nothing to expand — skip
      g.style.cursor = 'pointer';
      g.setAttribute('title', `Clic: añadir ${neighbors.length} tabla(s) FK de "${realName}"`);
      g.addEventListener('click', e => { e.stopPropagation(); SD.app.schemaExpand(realName); });
    });
  }

  function load(text, fileName) {
    let raw;
    try { raw = JSON.parse(text.replace(/^﻿/, '')); }   // quita BOM si lo trae
    catch (e) { alert('JSON inválido: ' + e.message); return; }
    let db;
    try { DATA = SD.shape(raw, null); }
    catch (e) { alert(e.message); return; }
    db = DATA.database;
    $('#subtitle').textContent = `${DATA.objects.length} objetos · ${DATA.tables.length} tablas · ${db}`;
    $('#search').disabled = false;
    document.body.classList.add('loaded');
    kindFilter = 'all'; dynOnly = false; schemaPinned = new Set();
    renderFilter();
    renderSidebar('');
    openOverview();
  }

  function readFile(file) {
    if (!file) return;
    const r = new FileReader();
    r.onload = () => load(r.result, file.name);
    r.readAsText(file);
  }

  // Demo auto-load: on a real server (GitHub Pages, any http(s) host) fetches
  // the bundled demo/graph_full.json (WideWorldImporters, public sample data)
  // so a first-time visitor sees the dashboard working immediately instead of
  // an empty upload screen. Silently does nothing if it fails - in particular,
  // opening index.html directly as file:// (the documented local workflow)
  // hits a CORS error on fetch() for local files in most browsers, which is
  // caught and ignored, leaving the normal upload/drop screen untouched.
  function tryLoadDemo() {
    fetch('demo/graph_full.json')
      .then(r => r.ok ? r.text() : Promise.reject())
      .then(text => {
        load(text, 'demo/graph_full.json');
        const banner = $('#demo-banner');
        if (banner) banner.hidden = false;
      })
      .catch(() => {});
  }

  // Modo claro/oscuro: preferencia persistida en localStorage, aplicada antes
  // de pintar nada (evita el "flash" del tema por defecto al recargar).
  function applyTheme(light) {
    document.body.classList.toggle('light', light);
    const btn = $('#theme-toggle');
    if (btn) btn.textContent = light ? '☀️' : '🌙';
  }
  function toggleTheme() {
    const light = !document.body.classList.contains('light');
    localStorage.setItem('sd-theme', light ? 'light' : 'dark');
    applyTheme(light);
  }

  function init() {
    applyTheme(localStorage.getItem('sd-theme') === 'light');
    $('#theme-toggle').addEventListener('click', toggleTheme);
    $('#file').addEventListener('change', e => readFile(e.target.files[0]));
    $('#search').addEventListener('input', e => renderSidebar(e.target.value));
    const dz = $('#drop');
    ['dragover', 'dragenter'].forEach(ev => document.addEventListener(ev, e => { e.preventDefault(); dz && dz.classList.add('hot'); }));
    ['dragleave'].forEach(ev => document.addEventListener(ev, e => { if (e.target === dz) dz.classList.remove('hot'); }));
    document.addEventListener('drop', e => { e.preventDefault(); dz && dz.classList.remove('hot'); readFile(e.dataTransfer.files[0]); });
    const uploadOwn = $('#demo-upload-own');
    if (uploadOwn) uploadOwn.addEventListener('click', e => {
      e.preventDefault();
      document.body.classList.remove('loaded');
      $('#demo-banner').hidden = true;
    });
    tryLoadDemo();
  }

  SD.app = { init, load, openObject, openOverview, openRisks, openSchema, setFilter,
             schemaAdd, schemaRemove, schemaExpand, schemaClear, setImpactDepth };
  document.addEventListener('DOMContentLoaded', init);
})(window.SD = window.SD || {});
