---
title: Plan de arquitectura
description: Plan de ejecución de la Fase 0/1/2 de la solución, con rutas, contrato y gate por paso.
read_when: Para saber qué queda por hacer y en qué orden, o antes de mover código entre proyectos.
related: [docs/ARQUITECTURA.md, docs/PATRONES.md, docs/BITACORA.md]
stability: volatile
updated: 2026-08-21
---

# Plan de diseño de la solución

Alcance: la solución completa (`Parser.Contracts`, `TSqlParser`, `NetParser`,
`ParserGeneral`), no un rincón. Documento de ejecución: cada fase lleva rutas exactas,
contrato y gate.

## 0. Diagnóstico medido (2026-08-21)

Reparto real de `src/TSqlParser` (13.762 líneas), por dependencia técnica:

| Categoría | Ficheros | Líneas |
|---|---|---|
| **T-SQL / ScriptDom** (AstWalker, GraphExporter, SqlAnalyzer, TableAnalyzer, SqlText, SqlFileLoader, FilterRuleClassifier, OperatorClassifier) | 8 | **6.124** |
| **Agnóstico del lenguaje** — solo `GraphPayload` (SqliteExporter, NodeStoreExporter, Graphify/GraphMl, Risk, ChangeMap×2, Audit×2, BlindRefs, AgentBench, Report, Mcp×2, Models, InputAnalyzer, Utf8Io) | 17 | **5.051** |
| **SQL Server vivo** — `SqlClient` (ObjectExtractor, TableSchemaExtractor, DbValidator, XePlanCaptor, CorpusRefresher, SqlConnections, ExecutionPlan/PlanEnricher, CorpusManifest) | 9 | **2.075** |
| `Parser.Contracts` | 4 | 182 |

Otras señales:

| Señal | Valor |
|---|---|
| Clases estáticas / ficheros | 29 de 36 |
| Interfaces en toda la solución | 1 (`IGraphExtractor`) |
| `GraphExporter.Build` | ~1167 líneas, un solo método |
| `AstWalker.Walk` / `AlterDetail` | ~669 / ~437 líneas |
| `File.`/`Console.`/`new SqlConnection` dentro de lógica | Program.cs 52, CorpusRefresher 37, AgentBench 22 |

### El defecto de solución

**El 37 % de `TSqlParser` no es T-SQL.** Toda la capa de grafo — exportadores, riesgo,
change-map, auditoría, MCP — vive dentro del proyecto de ScriptDom por inercia histórica,
y es exactamente la capa que `NetParser` y `ParserGeneral` necesitan.

Consecuencia comprobada, no teórica: `SqliteExporter.Write` **solo se llama desde
`TSqlParser/Program.cs`**. `ParserGeneral` fusiona el grafo SQL con el de .NET y escribe
`graph_full.json`, pero **el grafo unificado nunca llega a SQLite**, porque para exportarlo
tendría que arrastrar ScriptDom entero. De ahí que el MCP hoy solo pueda ver la parte SQL
de un grafo que ya es unificado por diseño.

Ese es el problema a resolver. Lo demás (Strategy, carpetas, métodos largos) es
consecuencia o cosmética.

### Regla que gobierna todo

**Refactor con red y guiado por dolor medido.** El activo son 256+43 tests verdes y
98,7675 % de recall laxo; ningún cambio de este documento puede moverlos. Nada de
repositorio genérico, contenedor de DI ni fábricas abstractas: no hay segundo cliente de
casi nada, y abstraer sin él es coste sin retorno.

**Regla del campamento**: de T17 en adelante el código nuevo nace con el patrón; el viejo
se refactoriza cuando se toca. Cero refactor especulativo.

---

## 1. Arquitectura objetivo

```
Parser.Contracts        Modelo + vocabulario + StoreSchema. CERO dependencias.
   ▲                    GraphNode/Rel/Payload, Vocab, Boundary, IGraphExtractor
   │                    + StoreSchema.cs (NUEVO)
   │
Parser.Graph  (NUEVO)   Todo lo agnóstico del lenguaje. Depende SOLO de Contracts
   ▲                    (+ Microsoft.Data.Sqlite). ~5.000 líneas que hoy están presas.
   │                    Export/ Analysis/ ChangeMap/ Bench/
   │
   ├── Parser.Mcp (NUEVO)      Lee el store. Contracts + Graph.StoreSchema + Sqlite.
   │
   ├── TSqlParser              Extractor T-SQL PURO: ScriptDom → GraphPayload.
   │      └── Live/            SQL Server vivo: ISqlCatalog + SqlClient.
   │
   ├── NetParser               Extractor C# (ya existe)
   │
   └── ParserGeneral / Cli     Composición: extractores + sinks + subcomandos
```

Cuatro consecuencias que convierten esto en diseño y no en mudanza de carpetas:

1. **`ParserGeneral` puede escribir SQLite del grafo unificado.** El MCP pasa a ver nodos
   `AppMethod` y aristas `EXECUTES_SQL` **con las mismas herramientas, sin escribir ni
   una nueva**. Hoy es imposible sin arrastrar ScriptDom.
2. **`IGraphExtractor` pasa a ser la interfaz de extensión real.** Un tercer extractor
   (SSIS, Python, lo que venga) se enchufa sin tocar nada existente.
3. **La dependencia se invierte donde debe**: los extractores dejan de conocer los sinks.
   Hoy `TSqlParser` es extractor, exportador y CLI a la vez.
4. **El compilador se convierte en el gate.** Si `Parser.Graph` no referencia ScriptDom,
   la fuga es imposible por construcción. Varios de los tests de convención que iban a
   hacer falta dejan de hacer falta.

### Coste real de la migración

Los ficheros **no cambian de contenido**: cambian de proyecto y de `namespace`. El truco
que lo abarata: `GlobalUsings.cs` ya existe en `TSqlParser`. Añadir allí
`global using Parser.Graph;` deja **los 256 tests y `Program.cs` compilando sin tocar una
sola línea**. La migración es `git mv` + un `.csproj` + una línea de global using.

---

## 2. Dónde encaja Strategy (y dónde no)

Distinción que decide cada caso: **Strategy = elegir una de N alternativas
intercambiables**. **Pipeline = ejecutar las N en orden**. `Build` y `AstWalker` no son
Strategy, y tratarlos como tal sería el error caro.

### 2.1 `IGraphSink` — exportadores  ← el más barato, empezar aquí

`src/TSqlParser/Program.cs:444-497`: cuatro bloques casi idénticos (`--graphify`,
`--graphml`, `--nodestore`, `--sqlite`), cada uno recalculando el nombre de la BD desde
`results` y repitiendo "quita `.json`, añade extensión".

```csharp
interface IGraphSink {
    string Flag { get; }          // "--sqlite"
    string Extension { get; }     // ".db"
    string Write(GraphPayload g, ExportContext ctx);   // devuelve la línea de resumen
}
```

Vive en `Parser.Graph/Export/`. `Program` queda en un `foreach` sobre los sinks cuyo flag
está presente. Riesgo casi nulo: los exportadores ya tienen la forma correcta (grafo →
fichero). Y es lo que permite que `ParserGeneral` exporte igual que `TSqlParser` sin
duplicar nada — o sea, es el punto 1 de §1 hecho realidad.

### 2.2 `IMcpTool` — herramientas MCP  ← hacer ANTES de T17

`McpServer.cs` tiene el esquema en un array literal (`ToolsList`) **y** el despacho en un
`switch` (`ToolsCall`): dos sitios por herramienta y un fallo latente (añadir a uno y
olvidar el otro). Con T17+T18 entrando de 2 a 4 herramientas, ahora.

```csharp
interface IMcpTool {
    string Name { get; }
    object Schema { get; }
    Dictionary<string, object?> Handle(SqliteConnection conn, JsonObject args);
}
```

Ganancia real: **el gate de 2 KB pasa a ser un bucle sobre el registro** en vez de un test
por herramienta escrito a mano. Cubre las que aún no existen.

### 2.3 `IRiskRule` — reglas de auditoría

`RiskAnalyzer.Analyze` devuelve `List<RiskFinding>` desde una función. Una clase por regla
en `Parser.Graph/Analysis/Rules/`, con severidad, categoría y test propio. Valor de
producto: las reglas de bad-practices se movieron del JS del dashboard a C# justo para
reusarlas; con registro, la herramienta MCP `risks`, el dashboard y el gate de CI consumen
el mismo conjunto, y añadir una regla es una clase más un test, no cirugía. Entra junto
con la herramienta `risks`.

### 2.4 `IObjectSource` — orígenes de entrada

`extract` (BD viva) / `from-sql` (ficheros) / `input.json` a mano → `IEnumerable<SourceObject>`.
Deja que los tests alimenten objetos sin tocar disco.

### 2.5 Lo que NO es Strategy

- `AstWalker` → **Visitor**, y ScriptDom ya lo regala (`TSqlFragmentVisitor`). Hoy hay un
  switch manual sobre tipos de sentencia reimplementando a mano ese visitor. Migrar por
  familias de sentencias: sentencia nueva = clase nueva. Por tramos, con prueba por
  mutación en cada uno.
- `GraphExporter.Build` → **pipeline de pasos**: `IGraphBuildStep.Apply(BuildContext)` y
  `Build` pasa a ser una lista ordenada (objetos → pasos → columnas → expansión de vistas
  → jerarquía). Cada paso testeable aislado.

---

## 3. Carpetas dentro de cada proyecto

Regla: **la carpeta no cambia el namespace** dentro de un mismo proyecto (C# no los ata);
el namespace solo cambia al cruzar a otro proyecto, que es donde el compilador hace
cumplir la frontera.

```
Parser.Contracts/     GraphModel, Vocab, Boundary, IGraphExtractor, StoreSchema
Parser.Graph/
  Export/             IGraphSink + Sqlite/NodeStore/Graphify/GraphMl/Neo4jJson
  Analysis/           RiskAnalyzer, Audit×2, BlindRefs, Report
    Rules/            IRiskRule + una clase por regla
  ChangeMap/          ChangeMapExporter, ChangeMapDiff
  Bench/              AgentBench
Parser.Mcp/
  Tools/              IMcpTool + una clase por herramienta
  McpServer.cs
TSqlParser/
  Model/              Models.cs partido (SourceObject, FlowLinkInfo, ObjectResult, ...)
  Parsing/            SqlAnalyzer, AstWalker, SqlText, InputAnalyzer, TableAnalyzer
    Visitors/         (fase 2) un visitor por familia de sentencia
  Graph/              GraphExporter
    Steps/            (fase 2) IGraphBuildStep + un paso por responsabilidad
  Live/               ISqlCatalog, ObjectExtractor, TableSchemaExtractor, DbValidator,
                      SqlConnections, XePlanCaptor, CorpusRefresher, CorpusManifest
  Plans/              ExecutionPlanParser, PlanEnricher
Cli/                  ISubcommand + un fichero por subcomando; Program.cs = registro
```

`Program.cs` son 505 líneas de `if` encadenados parseando `args` → patrón **Command**
(`ISubcommand { Name, Run(args) }` + registro).

---

## 4. DIP: solo en el acceso a datos

`ISqlCatalog` (sys.sql_modules, sys.columns, sys.foreign_keys) con implementación real y
otra en memoria. Consecuencia medible: convierte tests que hoy exigen contenedor en tests
normales, y deja el contenedor para el gate de verdad. Es la única inversión de
dependencia que se paga sola.

---

## 5. ¿Es SQLite la mejor opción?

Sí para lo que el MCP necesita, y conviene tener escrito el porqué y el límite.

Requisitos reales: un fichero, cero instalación, solo lectura, embebible en un
`dotnet tool`, consultas ad hoc de grafo, respuestas pequeñas.

| Opción | Veredicto |
|---|---|
| **SQLite** | Cero configuración, un fichero (2,4 MB para WWI), `Microsoft.Data.Sqlite` ya referenciado, CTEs recursivas (las usa `@col_provenance`), `json_extract` sobre `props`, modo solo-lectura real, 8 índices ya creados. **Elegida.** |
| nodestore / JSON | Ya existe y sirve para que un agente lea ficheros sueltos, pero sin motor de consulta cada pregunta nueva es código nuevo. Complementario. |
| DuckDB | Mejor para agregaciones sobre corpus grandes. La carga real es recorrido de grafo y búsquedas puntuales, no escaneos. Dependencia mayor sin retorno. |
| Neo4j / Cypher | El modelo natural del linaje, pero exige servidor: mata "un fichero en local". Ya cubierto como salida opcional vía `--graphify` → Cypher. |
| Grafo en memoria | Lo más rápido, pero sin artefacto que versionar, difundir ni diffear. |

Límites, sin adornar:

- El recorrido transitivo lo hace `Impact` en C# (BFS por lotes de 500 en el `IN`), no SQL.
  Correcto y acotado, pero es código, no consulta.
- **A verificar, no asumir**: `QueryNeighbors` usa `src LIKE $p || '#%'` para enrollar el
  Step a su objeto. SQLite solo optimiza `LIKE 'prefijo%'` con índice bajo condiciones de
  collation que aquí probablemente no se cumplen, así que **`ix_edges_src` podría no
  usarse** y esa rama ser un escaneo completo. Comprobar con `EXPLAIN QUERY PLAN` sobre
  `out/graph_full.db`. Si se confirma, el arreglo exacto y equivalente es el predicado de
  rango `src >= $p || '#' AND src < $p || '$'`. Alternativa: columna `owner_src` indexada.
- Ningún índice sobre `nodes(name)`: el `LIKE '%needle%'` de `resolve_object` es escaneo
  por diseño y no hay índice que lo arregle. Si molesta algún día, FTS5.

---

## 6. Cómo aprende el usuario a conectarse y a usarlo  ← hueco abierto hoy

Estado actual: **nada**. No hay sección en ningún README, el `--help` del subcomando es una
línea, y para tener un `graph_full.db` hay que conocer de antemano un pipeline de tres
comandos.

Y hay una confusión que la documentación debe deshacer en la primera frase: **el MCP no se
conecta a SQL Server**; lee un fichero SQLite ya generado. La conexión ocurre antes, en la
extracción (`--server`, integrated security, o `TSQL_SQL_USER`/`TSQL_SQL_PASSWORD`). Quien
no lo entienda buscará dónde poner la cadena de conexión en el cliente MCP y no la
encontrará.

Por orden de impacto:

1. **`quickstart <servidor> <basedatos> [--out <dir>]`** — encadena `extract` → análisis →
   `--columns --sqlite` y al terminar **imprime el bloque JSON de configuración del cliente
   MCP con la ruta absoluta ya rellenada**, listo para pegar. Convierte "leerse tres
   comandos" en "copiar y pegar". Variante: `quickstart --from-sql <dir>`.
2. **El error enseña.** Hoy, sin `--store` válido, sale `No existe la base SQLite '<ruta>'.`
   y ahí muere. Debe imprimir el comando exacto que la genera.
3. **`store_info`** como herramienta MCP: expone `meta` (`database`, `generated_at`,
   `node_count`, `edge_count`) más conteos por label. La tabla existe y **ninguna
   herramienta la expone**: un store viejo miente en silencio.
4. **Prompts MCP** (`prompts/list`, `prompts/get`): "onboarding de esta base", "auditoría
   de un cambio". Aparecen en el cliente como comandos y **el usuario no necesita conocer
   ninguna herramienta**. Es la respuesta directa a "cómo va a saber usarlo".
5. **T19**: sección en los dos README con el diagrama de las tres etapas (origen →
   `graph_full.db` → MCP), snippet de `claude mcp add` / config de Cursor, y guion de demo
   de 30 s.

---

## 7. Catálogo de herramientas MCP objetivo

Aviso de diseño: el número de herramientas es coste de contexto — `tools/list` viaja en
cada turno del agente. No meter doce. Conjunto real de nueve:

| Herramienta | Estado | Uso |
|---|---|---|
| `resolve_object` | hecha | nombre suelto → id canónico |
| `impact` | hecha | qué se rompe / de qué depende |
| `store_info` | nueva | fecha y tamaño del grafo; evita que un store viejo mienta |
| `describe_object` | nueva | la ficha del objeto. **Peldaño que falta**: hoy el agente resuelve un id y no puede leer el objeto |
| `column_impact` / `column_provenance` | T17 | con buckets Seguro/Probable/No lo sé |
| `diff_impact` | T18 | sobre `change_map`; `ChangeMapDiff` ya existe |
| `risks` | nueva | `RiskAnalyzer` ya escrito, no expuesto. Mayor ganancia rápida |
| `blind_spots` | nueva | dónde el motor NO ve (dinámico sin resolver + ciegas) |

`evidence(objeto, tabla|columna)` queda en el banquillo: los Step guardan `line_no`,
`action`, `target_name`, `detail`, `condition_path`, pero **no el SQL literal** (solo
`dynamic_sql` truncado a 200). Sería "línea y paso", no el texto. Aun así es la herramienta
que separa a un agente creíble de uno que afirma — decidir si merece la pena.

---

## 8. Gates

Con la arquitectura de §1, varios se vuelven innecesarios (los hace cumplir el
compilador). Quedan:

- Test que falle si un método supera N líneas. Umbral inicial por encima de lo actual
  (p. ej. 1200) y se baja en cada fase. Un número, no un criterio.
- Test que afirme `ImpactEdgeTypes ⊆ Vocab.KnownEdgeTypes`. Hoy `McpTools` reescribe el
  vocabulario como literales: un renombrado en el contrato no rompe la compilación y deja
  el MCP devolviendo `affected:[]` en silencio.
- Test que recorra el registro de sinks / reglas / herramientas y verifique que ninguno se
  despacha desde un `switch`.
- `EXPLAIN QUERY PLAN` de `QueryNeighbors` fijado en un test: si deja de usar índice, salta.

---

## 9. Cola de trabajo (actualizada 2026-08-21)

**La fase 0 de arquitectura está HECHA y publicada.** `Parser.Graph` y `Parser.Mcp`
existen, con la frontera impuesta por el compilador. El MCP tiene 9 herramientas. Las
ciegas de columna del corpus DNN bajaron de 90 a 22 (recall 99,6987 %) sin tocar la
precisión. Detalle en `docs/BITACORA.md`.

Principio de troceado, que sigue vigente: **cada paso compila, pasa la suite y se commitea
solo**. Un corte por cuota entre pasos deja el árbol sano; dentro de un paso, no.

### Fase A — cerrar el producto (lo que lo hace enseñable)

| # | Tarea | Complejidad | Perfil |
|---|---|---|---|
| A1 | **`evidence(objeto, tabla\|columna)`** — objeto + `line_no` + etiqueta del paso | baja-media | medio |
| A2 | **`remediation_plan(id)`** — los dos órdenes topológicos, con conflictos y ciclos declarados | **alta** | diseño humano, implementación medio |
| A3 | **Informe de auditoría end-to-end** sobre DNN, como demo real | media | medio |

**A1 desbloquea el resto**: sin evidencia por hallazgo, el informe es una lista de
afirmaciones que nadie puede comprobar. Los `Step` ya guardan `line_no`, `action`,
`target_name` y `condition_path`, y **ninguna herramienta los expone**.

**A2 es lo que ningún competidor puede hacer** (ordenar por dependencia real y no por
severidad) y también lo más difícil. Depende de A1. El diseño está en
`docs/auditoria-plantilla.md`, sección del plan de tareas.

### Fase B — el foso

| # | Tarea | Complejidad | Perfil |
|---|---|---|---|
| B1 | Reclasificar las 22 ciegas | baja | medio |
| B2 | **Mapa de cobertura de scopes**: qué tipos de nodo reciben resolución de columnas | media | medio |
| B3 | `ORDER BY` con detección de alias de salida | media, **riesgo medio** | medio |
| B4 | Planes de ejecución sobre el SQL dinámico | media-alta | medio |

**B2 cambia el método.** Hoy descubrir un hueco exige leer `AstWalker` entero. Un test que
enumere los tipos de nodo con scope propio y verifique cuáles están cubiertos convierte el
diagnóstico en **leer una tabla**. Es la lección de la sesión hecha instrumento.

**B3 ya falló una vez**: metía deducciones en la clase `direct` y tumbaba su precisión. El
intento está en `stash@{0}` con el diagnóstico.

**B4 desbloquea además** la sección de rendimiento del informe, que hoy se declara no
evaluable por falta de evidencia.

### Fase C — higiene con retorno medible

| # | Tarea | Complejidad | Perfil |
|---|---|---|---|
| C1 | **Barrido del descarte silencioso**: `new List<>()` vacíos, `return` tempranos, `continue` sin registrar | media | medio |
| C2 | Medir cuántos bytes ocupa `tools/list` completo | baja | bajo |
| C3 | Registrar coste real por tarea delegada | baja | proceso |

**C1 tiene la mejor relación esfuerzo/hallazgo de la lista.** Cinco casos aparecieron en una
sola sesión, todos del mismo patrón (ver `docs/BITACORA.md`). Buscarlo explícitamente en
vez de tropezarlo.

### Paralelización

- **Ola 1**: A1 + B1 + C2 — rutas disjuntas, se lanzan juntas.
- **Ola 2**: A3 + B4 — disjuntas entre sí.
- **Solas**: A2 (exige diseño previo), B3 y C1 (las dos tocan `AstWalker`).
- **Nunca juntas**: dos tareas sobre `AstWalker`. Aprendido a base de conflictos.

Regla operativa del multiagente: **el coordinador cablea `McpToolRegistry` y los contadores
congelados de `BlindRefsTests`**; a los agentes se les prohíbe tocarlos y devuelven las
cifras. Eso cazó dos defectos que los agentes no podían ver.

### Fuera de alcance, y por qué

- **Herramientas MCP nuevas**: nueve es el techo hasta que C2 diga lo que cuesta el catálogo
  en cada turno del agente.
- **Reglas de riesgo nuevas**: las 22 existentes tienen **2 controles negativos**. Sin medir
  falsos positivos, añadir es empeorar.
- **Capa de inferencia por LLM**: va después de B4, por el orden razonado en
  `docs/BITACORA.md`.
- **Fase 2 del refactor** (`GraphExporter.Build`, visitors de `AstWalker`): sigue reservada
  para hacerse con red y por tramos, y `AstWalker` ha subido de valor esta sesión, así que
  su listón sube también.

