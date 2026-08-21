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

## 9. Orden de ejecución

Principio de troceado: **cada paso compila, pasa la suite y se commitea solo**. Nada de
mover 5.000 líneas de una vez. Un corte por cuota entre pasos deja el árbol sano; un corte
*dentro* de un paso, no. Por eso los pasos se dimensionan por grupo cohesionado, no por
comodidad.

**Fase 0 — la solución** (antes de T17). Ocho pasos, ninguno grande:

| # | Paso | Ficheros | Gate |
|---|---|---|---|
| 0.1 | `Parser.Graph` vacío + `.csproj` + referencia desde `TSqlParser` + `global using Parser.Graph;` en `GlobalUsings.cs` | 0 movidos | compila, 256/256 |
| 0.2 | Mover `Export/`: Sqlite, Graphify, GraphMl, Utf8Io | 4 | 256/256 sin tocar un test |
| 0.3 | Mover `NodeStoreExporter` (1198 líneas, va solo) | 1 | 256/256 |
| 0.4 | Mover `Analysis/`: Risk, Audit×2, BlindRefs, Report | 5 | 256/256 |
| 0.5 | Mover `ChangeMap/` + `Bench/` + `Models`/`InputAnalyzer` | 5 | 256/256 |
| 0.6 | `StoreSchema.cs` en Contracts + gate `ImpactEdgeTypes ⊆ Vocab.KnownEdgeTypes` | 1 nuevo | gate nuevo en verde |
| 0.7 | `Parser.Mcp`: mover McpServer + McpTools | 2 | 256/256 |
| 0.8 | `IMcpTool` + registro; `IGraphSink` | refactor | gate de 2 KB pasa a bucle sobre el registro |

Y entonces, **la validación de que la arquitectura sirve para algo** (no un extra):

| # | Paso | Por qué |
|---|---|---|
| 0.9 | `ParserGeneral` escribe SQLite del grafo unificado | El MCP ve SQL + .NET con las herramientas que ya existen. Si esto no sale fácil, la arquitectura de §1 está mal y hay que revisarla antes de seguir. |

**Fase 1 — producto**:
5. T17 `column_impact`/`column_provenance`; T18 `diff_impact`.
6. `store_info` + `describe_object`.
7. `quickstart` + error que enseña + prompts MCP + T19.
8. `IRiskRule` + registro, con la herramienta `risks` y `blind_spots`.
9. `ISubcommand` en `Cli/`.

**Fase 2 — con red, por tramos, prueba por mutación en cada uno**:
10. `IGraphBuildStep`: partir `Build`.
11. Visitors de ScriptDom: partir `AstWalker`.
12. `ISqlCatalog` + `TSqlParser/Live/`.

---

## Nota

`notes/` está ignorado por git. Si este plan debe sobrevivir fuera de esta máquina, su
sitio es `docs/`. Misma decisión pendiente que la de `test/pr-impact-demo`.
