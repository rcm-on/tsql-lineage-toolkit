# SQL Dashboard

App visual **autónoma** (sin build, sin dependencias, offline) para explorar el
análisis del `tsql-parser`. **Subes el `workflows_full.json`** y la app construye
sola: resumen general, navegación por objeto, flujo de control en lenguaje natural,
llamadas (entrantes y salientes), variables y mini-gráficos — con mini-resúmenes
automáticos para no tener que pedirlos.

## Uso

1. Genera el análisis con el parser:
   ```
   cd ../src/TSqlParser
   dotnet run -- input.json graph_full.json workflows_full.json --columns
   ```
2. Abre `index.html` en el navegador (doble clic).
3. Sube (o arrastra) el `workflows_full.json` generado.

Con eso basta: "quién me llama" se deriva invirtiendo los `ExecCalls` de todos los
objetos, así que **un solo fichero** es suficiente.

## Estructura (componentes en ficheros separados)

| Fichero | Responsabilidad |
|---|---|
| `index.html` | Shell + zona de carga (upload/drag). |
| `src/style.css` | Estilos. |
| `src/naturalize.js` | Traduce predicados T-SQL a lenguaje natural. |
| `src/charts.js` | Mini-gráficos SVG/HTML (barras, donut, columnas). |
| `src/shape.js` | Transforma el JSON crudo → modelo (callsIn/out, árbol de flujo, vars). |
| `src/summary.js` | Mini-resúmenes automáticos (base y por objeto). |
| `src/components.js` | Componentes: Sidebar, Overview, ObjectView, FlowTree, Summary. |
| `src/app.js` | Carga, estado, routing y cableado. |

Todo en cliente (vanilla JS, sin framework ni build): los `<script>` se cargan en
orden de dependencia y comparten el namespace global `SD`.
