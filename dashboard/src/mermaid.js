// Integración de Mermaid (vendorizado en vendor/mermaid.min.js, UMD -> globalThis.mermaid).
// Genera bloques <div class="mermaid"> con una barra de herramientas (guardar
// .mmd / SVG / PNG, copiar texto) y los renderiza diferidamente. Expuesto como SD.mm.
(function (SD) {
  let seq = 0;
  let initDone = false;

  function init() {
    if (initDone) return;
    initDone = true;
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'loose',
      theme: 'dark',
      flowchart: { useMaxWidth: false },
    });
  }

  // Codifica texto UTF-8 en base64 para guardarlo como atributo data-src
  // (evita escapar comillas/`<`/`>` dentro del HTML del bloque).
  const b64encode = s => btoa(unescape(encodeURIComponent(s)));
  const b64decode = s => decodeURIComponent(escape(atob(s)));

  // Bloque con barra de herramientas + contenedor a renderizar. `def` es el
  // texto Mermaid (flowchart TD/LR...); `title` se usa para nombrar los exports.
  function block(def, title) {
    const id = 'mm-' + (++seq);
    const safeTitle = (title || 'diagrama').replace(/[^\w.-]+/g, '_');
    return `<div class="mm-wrap">
      <div class="mm-bar">
        <span class="mm-title">${SD.charts.esc(title || '')}</span>
        <span class="mm-tools">
          <button onclick="SD.mm.saveMmd('${id}','${safeTitle}')" title="Guardar como .mmd">.mmd</button>
          <button onclick="SD.mm.saveSvg('${id}','${safeTitle}')" title="Guardar como SVG">SVG</button>
          <button onclick="SD.mm.savePng('${id}','${safeTitle}')" title="Guardar como PNG">PNG</button>
          <button onclick="SD.mm.copyDef('${id}')" title="Copiar definición Mermaid">copiar</button>
        </span>
      </div>
      <div class="mermaid" id="${id}" data-src="${b64encode(def)}" data-title="${SD.charts.esc(safeTitle)}"></div>
    </div>`;
  }

  // Renderiza todos los `.mermaid[data-src]` pendientes dentro de `root` (o
  // todo el documento). Idempotente: marca data-rendered=1 tras renderizar.
  async function renderAll(root) {
    init();
    const scope = root || document;
    const els = scope.querySelectorAll('.mermaid[data-src]:not([data-rendered])');
    for (const el of els) {
      const def = b64decode(el.dataset.src);
      try {
        const { svg, bindFunctions } = await mermaid.render(el.id + '-svg', def);
        el.innerHTML = svg;
        if (bindFunctions) bindFunctions(el);
        el.dataset.rendered = '1';
      } catch (e) {
        el.innerHTML = `<span class="muted">Error al renderizar diagrama: ${SD.charts.esc(e.message || String(e))}</span>`;
        el.dataset.rendered = '1';
      }
    }
  }

  function download(blob, filename) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click(); a.remove();
    URL.revokeObjectURL(url);
  }

  function saveMmd(id, title) {
    const el = document.getElementById(id); if (!el) return;
    download(new Blob([b64decode(el.dataset.src)], { type: 'text/plain' }), `${title}.mmd`);
  }

  function getSvgEl(id) {
    const el = document.getElementById(id);
    return el ? el.querySelector('svg') : null;
  }

  function saveSvg(id, title) {
    const svg = getSvgEl(id); if (!svg) return;
    const xml = new XMLSerializer().serializeToString(svg);
    download(new Blob([`<?xml version="1.0" encoding="UTF-8"?>\n${xml}`], { type: 'image/svg+xml' }), `${title}.svg`);
  }

  // Rasteriza el SVG renderizado a PNG (fondo #1e1e1e, escala 2x para nitidez).
  function savePng(id, title) {
    const svg = getSvgEl(id); if (!svg) return;
    const xml = new XMLSerializer().serializeToString(svg);
    const box = svg.viewBox && svg.viewBox.baseVal && svg.viewBox.baseVal.width
      ? svg.viewBox.baseVal : { width: svg.width.baseVal.value, height: svg.height.baseVal.value };
    const scale = 2;
    const img = new Image();
    const url = URL.createObjectURL(new Blob([xml], { type: 'image/svg+xml' }));
    img.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(box.width * scale));
      canvas.height = Math.max(1, Math.round(box.height * scale));
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#1e1e1e';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
      URL.revokeObjectURL(url);
      canvas.toBlob(blob => download(blob, `${title}.png`), 'image/png');
    };
    img.src = url;
  }

  function copyDef(id) {
    const el = document.getElementById(id); if (!el) return;
    navigator.clipboard.writeText(b64decode(el.dataset.src)).catch(() => {});
  }

  SD.mm = { init, block, renderAll, saveMmd, saveSvg, savePng, copyDef };

  // Punto de entrada para los `click n call sdOpen("dbo.X")` de los diagramas Mermaid.
  window.sdOpen = name => { if (SD.app) SD.app.openObject(name); };
})(window.SD = window.SD || {});
