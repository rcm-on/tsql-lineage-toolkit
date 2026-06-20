# SQL Dashboard

App visual **autónoma** (sin build, sin dependencias, offline) para explorar el
análisis del `tsql-parser`. **Subes el `graph_full.json`** (o lo arrastras) y la
app construye sola: resumen general, navegación por objeto/tabla, flujo de
control en lenguaje natural, cadena de impacto multi-nivel, riesgos de código,
esquema ORM interactivo y mini-gráficos — con mini-resúmenes automáticos para
no tener que pedirlos.

## Uso

1. Genera el análisis con el parser:

   ```bash
   cd ../src/TSqlParser
   dotnet run -- input.json graph_full.json --columns
   ```

2. Abre `index.html` en el navegador (doble clic, no necesita servidor).
3. Sube (o arrastra) el `graph_full.json` generado.

Con eso basta: "quién me llama" se deriva invirtiendo los `CALLS` de todos los
objetos, así que **un solo fichero** es suficiente.

## Vistas principales

- **Resumen general** — estadísticas de la base de datos, ranking de
  complejidad, tablas más escritas, donut de tx/errores/cursor/SQL dinámico.
- **Vista de objeto** (procedimiento/función/trigger/vista) — métricas
  (complejidad ciclomática, profundidad de anidación, nº de pasos), riesgos
  del objeto, a quién llama / quién le llama, tablas leídas/escritas,
  variables y su construcción de SQL dinámico, parámetros.
- **Flujograma de control** (Mermaid `flowchart TD`) — IF/WHILE/TRY como
  ramas reales, con SQL dinámico anotado.
- **Árbol de texto** (condiciones en lenguaje natural) — el mismo flujo en
  formato lista, con cada `EXEC` **expandido recursivamente en los pasos del
  procedimiento llamado**, hasta la profundidad elegida (1-5 niveles,
  selector compartido con la cadena de impacto). Si una llamada vuelve a un
  objeto que ya está en la pila de llamadas activa, se marca como "↻
  recursión" en vez de expandirse infinitamente.
- **Cadena de impacto** (Mermaid `flowchart LR`) — BFS multi-nivel en ambas
  direcciones (qué afecta este objeto / qué lo alimenta), combinando `CALLS`
  con lecturas/escrituras de tabla. Máximo 8 nodos por nivel (se marca
  "…+más" si se trunca).
- **Flujo de datos** — entradas (parámetros IN + tablas leídas) → objeto →
  salidas (escrituras + parámetros OUT + llamadas EXEC).
- **Vista de tabla** — columnas, PK, lectores/escritores, relaciones FK,
  tally de operaciones.
- **Riesgos** — hallazgos agrupados por severidad (CRÍTICO/ALTO/MEDIO/BAJO/
  INFO) y categoría (Seguridad, Robustez, Rendimiento, Mantenibilidad,
  Integridad, Diseño): SQL dinámico no parametrizado, cursor sin TRY/CATCH,
  complejidad alta, anidación profunda, variables sin usar, tablas sin PK,
  tablas con muchos escritores, etc.
- **Esquema ORM** — diagrama ER interactivo: añade tablas desde un
  desplegable, haz clic en una tabla del diagrama para expandir sus FK,
  exporta como `.mmd` / SVG / PNG.

## Estructura (componentes en ficheros separados)

| Fichero | Responsabilidad |
|---|---|
| `index.html` | Shell + zona de carga (upload/drag). |
| `src/style.css` | Estilos. |
| `src/shape.js` | Transforma el JSON crudo (Nodes/Relationships) → modelo del dashboard (`byName`, callsIn/out, árbol de flujo, variables, columnas). |
| `src/naturalize.js` | Traduce predicados T-SQL a lenguaje natural (updates, EXISTS, transacción abierta, etc.). |
| `src/impact.js` | BFS multi-nivel para la cadena de impacto (`SD.impact.chain`), combinando CALLS + reads/writes en ambas direcciones. |
| `src/charts.js` | Mini-gráficos SVG/HTML sin librerías: barras, donut, tally, grafo de llamadas (`miniGraph`). |
| `src/risks.js` | Detección de riesgos/anti-patrones (`SD.risks.analyze`), 6 categorías. |
| `src/summary.js` | Mini-resúmenes automáticos (base de datos y por objeto/tabla). |
| `src/components.js` | Componentes de UI: Sidebar, Overview, ObjectView, TableView, `FlowTree` (árbol de texto con expansión recursiva de EXEC), `FlowChartMermaid`, `ImpactChainMermaid`, RisksView, SchemaView. |
| `src/mermaid.js` | Wrapper sobre `vendor/mermaid.min.js`: renderizado diferido + exportar `.mmd`/SVG/PNG. |
| `src/app.js` | Carga del JSON, estado, routing y cableado de eventos (`SD.app`). |

Todo en cliente (vanilla JS, sin framework ni build): los `<script>` se cargan en
orden de dependencia y comparten el namespace global `SD`.

## Pruebas end-to-end (Playwright)

```bash
cd e2e
npm ci                 # primera vez
npx playwright install # primera vez, si no hay navegadores cacheados
```

| Script | Qué hace | Salida |
|---|---|---|
| `node check-dashboard.js` | Smoke test: carga el dashboard, sube `samples/from-sql-demo/graph.json`, comprueba que no hay errores JS y que aparece la cadena de impacto. | `screenshot.png` |
| `node explore.js` | Abre un objeto concreto (`Sales.usp_UpdateCustomerEmail`) y lo capta. | `screenshot-object.png` |
| `node schema-test.js` | Prueba el Esquema ORM: estado vacío → añadir tabla → expandir FK. Requiere `eval/eval_graph_enriched.json`. | `docs/dashboard-schema-*.png` |
| `node screenshots.js` | Genera todas las capturas usadas en este README y en el README raíz del toolkit. | `docs/dashboard-*.png` |

## Ver también

- [docs/nodestore-analysis.md](../docs/nodestore-analysis.md) — comparativa medida (ficheros, bytes, saltos, tiempo) entre `graph_full.json` completo y el NodeStore (`--nodestore`), tanto para consultar como para actualizar tras editar un objeto.
- [docs/ai-agents.md](../docs/ai-agents.md) — coste en tokens del NodeStore para agentes de IA.
