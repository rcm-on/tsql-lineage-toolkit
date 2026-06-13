// Orquestador: carga (upload/drag) del workflows_full.json, estado, routing y
// cableado de los componentes. Expuesto como SD.app.
(function (SD) {
  const $ = s => document.querySelector(s);
  let DATA = null, current = null, kindFilter = 'all', dynOnly = false;

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
    $('#main').innerHTML = `<div class="nav"><button class="on" onclick="SD.app.openOverview()">◉ Resumen general</button></div>` + SD.components.Overview(DATA);
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
  }

  function openRisks() {
    current = null;
    document.querySelectorAll('.item').forEach(e => e.classList.remove('sel'));
    $('#main').innerHTML = `<div class="nav"><button onclick="SD.app.openOverview()">◉ Resumen general</button><button class="on">⚠ Riesgos</button></div><h2>Riesgos y malas prácticas</h2>` + SD.components.RisksView(DATA);
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
  }

  function openObject(name) {
    const e = DATA.byName[name]; if (!e) return;
    current = name;
    document.querySelectorAll('.item').forEach(el => el.classList.toggle('sel', el.dataset.n === name));
    const view = e.kind === 'table' ? SD.components.TableView(e, DATA) : SD.components.ObjectView(e, DATA);
    $('#main').innerHTML = `<div class="nav"><button onclick="SD.app.openOverview()">◉ Resumen general</button></div>` + view;
    $('#main').scrollTop = 0;
    SD.mm.renderAll($('#main'));
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
    kindFilter = 'all'; dynOnly = false;
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

  function init() {
    $('#file').addEventListener('change', e => readFile(e.target.files[0]));
    $('#search').addEventListener('input', e => renderSidebar(e.target.value));
    const dz = $('#drop');
    ['dragover', 'dragenter'].forEach(ev => document.addEventListener(ev, e => { e.preventDefault(); dz && dz.classList.add('hot'); }));
    ['dragleave'].forEach(ev => document.addEventListener(ev, e => { if (e.target === dz) dz.classList.remove('hot'); }));
    document.addEventListener('drop', e => { e.preventDefault(); dz && dz.classList.remove('hot'); readFile(e.dataTransfer.files[0]); });
  }

  SD.app = { init, load, openObject, openOverview, openRisks, setFilter };
  document.addEventListener('DOMContentLoaded', init);
})(window.SD = window.SD || {});
