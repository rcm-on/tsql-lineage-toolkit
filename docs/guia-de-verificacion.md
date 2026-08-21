# Guía de verificación — qué hace falta para dar el OK a un cambio

Checklist operativo, pensado para que **cualquier modelo** (no hace falta uno caro)
pueda ejecutarlo mecánicamente: cada paso trae el comando exacto, la salida
esperada literal, y el criterio de pase/fallo. Si un paso falla, la propia guía
dice cómo distinguir un fallo real de uno ambiental antes de tocar código.

**Regla general de todo el documento:** no declares "verificado" por leer un
mensaje de commit o un informe de otro agente. Ejecuta el comando tú mismo y
compara la salida literal contra lo que dice esta guía. Ver
`docs/corpus-multibase.md` → sección "Método" para el porqué.

**Cuándo correrla entera:** no en cada commit — es cara (los 4 corpus + las
capturas tardan varios minutos). Córrela completa antes de fusionar a `main`, o
cada cierto número de subidas acumuladas (a discreción: si se han integrado
varios arreglos seguidos sin verificación agregada, tócala). Para un commit
suelto basta con §1, §2 y el corpus que toque tu cambio; dilo explícitamente en
el informe si no corriste la guía completa.

**Este documento es vivo, no una foto fija.** Si en una verificación aparece un
gotcha nuevo (un falso fallo distinto, una trampa de entorno nueva, un corpus
adicional que se incorpore como control), añádelo a §11 o a la sección que
corresponda **en el mismo cambio** que lo descubrió — no lo dejes para luego.
La guía debe crecer con el proyecto o se queda obsoleta como le pasó a las
cifras del README más de una vez.

---

## 0. Antes de nada

1. Confirma en qué worktree/rama estás y que el árbol de trabajo está limpio:
   ```bash
   git status --short
   git branch --show-current
   ```
2. Si vas a tocar el motor (`src/TSqlParser/`), trabaja en un worktree dedicado,
   no en el árbol principal si otro cambio a medias lo tiene roto. Comprueba
   primero:
   ```bash
   dotnet build TSqlLineageToolkit.slnx
   ```
   Si esto ya falla con errores de compilación **antes** de tocar nada, el
   árbol estaba roto de antes — no es tuyo, no sigas ahí.

---

## 1. Compilación

```bash
dotnet build TSqlLineageToolkit.slnx
```

**Pasa si:** la última línea dice `0 Errores`. Los `Advertencia(s)` (warnings)
no bloquean — son ruido preexistente, no se corrigen como parte de una
verificación a menos que el cambio los introduzca.

---

## 2. Pruebas unitarias

```bash
dotnet test TSqlLineageToolkit.slnx
```

**Pasa si:** `Con error: 0` y el total coincide con el esperado (revisa el
número de pruebas del último `docs/ejecucion-canonica.md` — hoy son 197
TSqlParser + 23 NetParser = 220; si tu cambio añade pruebas, el total sube en
esa cantidad exacta).

### ⚠️ Falso fallo conocido: bloqueo de Smart App Control (Windows)

Si ves esto — y solo esto — **no es un fallo de tu código**:

```
Mensaje de error:
 System.IO.FileLoadException : Could not load file or assembly '...\TSqlParser.dll'.
 Una directiva de Control de aplicaciones bloqueó este archivo. (0x800711C7)
```

Es Windows Smart App Control bloqueando un `.dll` recién compilado sin
reputación (ver `sac-blocks-tsqlparser-dll` en memoria del proyecto). Señales
de que es esto y no una regresión real:
- El mensaje es literalmente ese, no una `Assert` fallida.
- `dotnet build` de ese mismo código dio `0 Errores`.
- Vuelve a correr `dotnet test` una o dos veces más: si el número de fallos
  baja o cambia de test cada vez (o llega a `0`), era esto. Si el **mismo**
  `Assert` falla con un mensaje de aserción (no `FileLoadException`), es real.

**No sigas reintentando indefinidamente** si tras 2-3 intentos sigue en el
mismo sitio: acepta el resultado parcial, dilo explícitamente en el informe, y
no lo cuentes como fallo de lógica.

### Índice de qué cubre cada fichero de test (para correr solo lo que aplica)

`dotnet test TSqlLineageToolkit.slnx` corre todo. Si tu cambio es puntual, usa
`--filter "FullyQualifiedName~<Clase>"` con la clase que corresponda de esta
tabla en vez de esperar la suite entera. Generado leyendo
`tests/TSqlParser.Tests/*.cs` directamente — si añades un fichero de test
nuevo, añade su fila aquí en el mismo cambio.

| Fichero | Qué verifica | ¿Necesita SQL Server vivo? |
|---|---|:---:|
| `LineageTests.cs` | Suite de regresión de patrones de lineage — el grueso de la cobertura (80 casos): SELECT/INSERT/UPDATE/DELETE/MERGE, SQL dinámico, cursores, CTEs | No |
| `NodeStoreUpdateTests.cs` | `NodeStoreExporter.Update` (reescritura incremental) vs. `Write` completo — que ambos caminos den el mismo resultado | No |
| `ColumnRecallGateTests.cs` | Lineage de columna contra catálogo externo, corpus DNN Platform/DotNetNuke (739 módulos + 128 tablas) — el trinquete más grande del proyecto | **Sí** (`LiveSql`) |
| `ViewLineageCatalogTests.cs` | Lineage a través de vistas vs. `sys.dm_sql_referenced_entities` en vivo | **Sí** (`LiveSql`) |
| `AuditorChallengeGateTests.cs` | Contrasta afirmaciones en prosa de `docs/claude-audit-report.md`/`docs/gemini-audit-report.md` contra cifras reales | **Sí** (`LiveSql`) |
| `BadPracticesGateTests.cs` | Gate del corpus `eval/bad-practices/` (anti-patrones con referencia `expected-findings.json`) | No |
| `CommunityEdgeCaseGateTests.cs` | Gate del corpus `eval/community-edge-cases/` (MERGE, CTEs recursivas, SQL dinámico, cursores) | No |
| `CteUnionFilterTests.cs` | `WHERE` dentro de CTE o de una rama de `UNION` (arreglo #11) | No |
| `DynamicExecViaVariableTests.cs` | `EXECUTE @variable` cross-database (patrón de Ola Hallengren) | No |
| `TableIdentityTests.cs` | Falso negativo real de Ola Hallengren: alias de `UPDATE` que coincide con nombre corto de tabla creaba un segundo nodo (arreglo #1) | No |
| `TableValuedFunctionReferenceTests.cs` | Referencias a TVF (propia, catálogo `sys.dm_*`, funciones de forma de tabla) antes invisibles (arreglo #8) | No |
| `InformationalOutputTests.cs` | `PRINT`/`RAISERROR` de severidad ≤10 no debe clasificarse como `THROW` (arreglo #9) | No |
| `JsonHygieneTests.cs` | BOM UTF-8 en artefactos, `coverage_pct` null sobre denominador cero (arreglos #4/#5) | No |
| `ExecutionPlanParserTempFilterTests.cs` | Filtro de tablas temporales/variable en `ExecutionPlanParser.CollectTableAccesses` (enrich-from-plans) | No |
| `XePlanCaptorCorrelationTests.cs` | Atribución `nest_level` de `XePlanCaptor.Correlate` (SQL dinámico vía Extended Events) | No |
| `ChangeMapTests.cs` / `ChangeMapDiffTests.cs` | `change_map.json` (workflows + impacto por objeto) y su diff para el gate de PR | No |
| `SqliteExporterTests.cs` | Export a SQLite: esquema poblado, columnas de auditoría promovidas | No |
| `AgentBenchTests.cs` | `bench-make`/`bench-grade`, el autotest del benchmark de agentes | No |
| `ChatGpt/*.cs` | Fuzzing, pruebas de estrés, casos límite del First Responder Kit y mejoras puntuales aportadas por otro modelo — tratar como regresión general | No |

Los marcados **Sí** necesitan `.\SQLEXPRESS` con WideWorldImporters/AdventureWorks2019 restauradas y viven detrás del trait `LiveSql` — es el motivo de que CI (`.github/workflows/ci.yml`) corra menos pruebas que en local (ver cifra exacta en `docs/ejecucion-canonica.md`).

---

## 3. Regresión multi-corpus

Cuatro corpus de control, cada uno con su cifra de referencia conocida
(consulta `docs/ejecucion-canonica.md` y `docs/corpus-multibase.md` para los
números vigentes — **no los copies de memoria, están para eso**):

```bash
cd src/TSqlParser

# WideWorldImporters (base viva)
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
dotnet run -- ../../input.json ../../out/graph_full.json --columns --sqlite --nodestore

# AdventureWorks2019 (base viva) — repite el patrón con --server
# Ola Hallengren / First Responder Kit (from-sql, sin servidor):
dotnet run -- <input.json del corpus> <salida.json> --columns --sqlite --nodestore
```

**Pasa si:**
- `Analyzed N objects (N ok, 0 parse errors)` — cero errores de parseo en los
  4 corpus, siempre.
- Nodos/aristas coinciden con la última cifra documentada, **o** si difieren,
  la diferencia tiene una causa explicable y documentada (ver la tabla
  "Movimiento respecto a la primera pasada" en `corpus-multibase.md` como
  plantilla de cómo explicar un delta).
- Si tu cambio **no** debería tocar un corpus concreto y ese corpus se mueve
  igual, es una señal de alarma — para y averigua por qué antes de continuar.

---

## 4. Contraste contra el catálogo vivo (`validate`)

Solo en las bases con servidor real (WWI, AdventureWorks2019):

```bash
dotnet run -- validate ../../out/graph_full.json --server .\SQLEXPRESS
```

**Pasa si**, para las dos secciones (`FK relationships` y `CALLS`):
```
In DB but missing from graph: 0
In graph but not in DB (within scope): 0
```
Cualquier número distinto de 0 en cualquiera de las dos líneas es un fallo
real: significa ausencias o aristas inventadas. No hay margen de tolerancia
aquí — es el resultado que sostiene toda la credibilidad de la herramienta.

---

## 5. Caso de control (no-regresión dirigida a un objeto conocido)

Además de las cifras agregadas, comprueba un objeto concreto que **no**
debería moverse con tu cambio. Ejemplo vigente:
`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` en WWI debe seguir
resolviendo **34 de 34** sentencias de SQL dinámico, todas con aristas
**ciertas** (sin `inferred`/`confidence` marcados):

```bash
node -e "
const o = require('<ruta>/graph_full.nodes/objects/WideWorldImporters_DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad/object.json');
const dyn = o.owned.steps.filter(s => s.properties.is_dynamic_sql);
console.log('dynamic steps:', dyn.length, '| unresolved:', dyn.filter(s => !s.properties.dynamic_sql).length);
"
```

**Pasa si:** `dynamic steps: 34 | unresolved: 0`, sin excepciones. Si tu
cambio toca resolución de SQL dinámico y este número baja, es una regresión
grave (falso negativo silencioso — la categoría de bug más peligrosa de este
proyecto).

---

## 6. Consistencia del NodeStore y los tres formatos de salida

```bash
node -e "
const idx = require('<ruta>/graph_full.nodes/index.json');
console.log('unknown_edge_types:', idx.stats.unknown_edge_types);
console.log('unknown_labels:', idx.stats.unknown_labels);
console.log('orphan_edges:', idx.stats.orphan_edges);
"
```

**Pasa si:** las tres listas/números salen vacíos/cero. Si aparece algo, el
motor emitió un tipo de arista o etiqueta que el NodeStore no declara — hay
que darlo de alta en el vocabulario cerrado (`Vocab.cs` /
`NodeStoreExporter.cs`), no ignorarlo.

Los tres formatos (`graph_full.json`, `graph_full.db` vía SQLite,
`graph_full.nodes/index.json`) deben reportar **el mismo** nodos/aristas. Si
no coinciden, alguno se generó en una pasada distinta — no lo mezcles en el
informe (ver incidencia #5 en `docs/ejecucion-canonica.md` §6 de la primera
ejecución canónica).

---

## 7. Verificación visual (capturas del dashboard)

**Lección de esta sesión: los datos pueden estar perfectos y la captura salir
rota igualmente — no te fíes del tamaño de fichero, mira la imagen.**

```bash
cd dashboard/e2e
npm install && npx playwright install chromium   # solo la primera vez
node shots-readme.js && node shots-diagrams.js
```

Abre cada `docs/readme-*.png` generado y comprueba a ojo:
- El diagrama de flujo va de **INICIO a FIN** sin huecos en blanco grandes
  (si el elemento a capturar es más alto que el viewport, revisa que
  `shotDiagram()` en `shots-diagrams.js` redimensione el viewport antes de
  capturar — es el bug que se arregló en `75f57e8`).
- Las condiciones con operadores de comparación (`<>`, `<=`, `>=`) se ven
  completas, no truncadas a solo el nombre de la variable (bug del mismo
  commit: `mmSanitize` se comía los ángulos).
- La cabecera del dashboard dice el número de objetos/tablas que toca (compara
  contra `audit_report.json` → `summary`).
- El panel de riesgos suma exactamente el total declarado (crítico+alto+medio+bajo).

Si algo de esto se ve mal, es un bug de renderizado del dashboard, no de las
cifras — no lo confundas con un problema de datos.

---

## 8. Comparación paso a paso contra el SQL fuente (AST vs. texto)

Para verificar que el AST distingue código real de texto-que-parece-código
(el argumento central de la herramienta), sobre el procedimiento más complejo
del corpus que estés tocando:

```bash
# Cuenta tokens IF en el texto crudo (incluye los que viven dentro de strings dinámicos)
grep -o '\bIF\b' <fuente.sql> | wc -l

# Compara contra lo que reporta el motor:
node -e "
const o = require('<objeto>/object.json');
console.log('flujos de control (AST):', o.properties.cyclomatic_complexity);
"
```

**Pasa si:** el AST reporta un número **menor** que el grep crudo, y la
diferencia son exactamente los tokens que viven dentro de los strings de SQL
dinámico reconstruido (verifícalo leyendo el fuente a mano en ese tramo, no lo
asumas). Si el AST reporta un número **mayor o igual** al grep crudo, algo
está mal — el AST nunca debería "ver" más `IF` que el texto tiene.

---

## 9. Verificación agente + JSON en paralelo

Esta es la prueba que importa para el caso de uso real de la herramienta (dar
de comer el grafo a un LLM). Con el mismo `graph_full.json` (o el NodeStore):

1. Formula una pregunta de lineage concreta y verificable de otra forma —
   p. ej. *"¿qué procedimientos escriben en `Warehouse.StockItems`?"*.
2. Pide la respuesta **dos veces, por caminos distintos**:
   - Al dashboard (panel del objeto tabla, sección "Referencias").
   - A un agente con **solo** el JSON/NodeStore como contexto (sin dashboard),
     pidiéndole que consulte `WRITES_TO`/`READS_FROM` desde el nodo de la tabla.
3. Verifica la respuesta contra una tercera vía independiente — `sqlcmd`
   directo (`sys.sql_expression_dependencies`) o una lectura manual del SQL.

**Pasa si:** las tres respuestas coinciden en el conjunto de objetos. Si el
agente-sobre-JSON da una lista distinta al dashboard (que leen el mismo
grafo), hay una inconsistencia entre cómo el dashboard interpreta las aristas
y cómo lo haría un consumidor ingenuo del JSON — revisa el `howto` del
`index.json`, probablemente le falta una aclaración.

---

## 10. Higiene de git antes de commitear

- `git status --short` antes de `git add` — nunca `git add -A`/`git add .` a
  ciegas en un repo con trabajo ajeno en curso.
- Excluye: scripts de diagnóstico sueltos (`diag*.js`), capturas de prueba que
  no referencia ninguna documentación, ficheros de notas operativas internas.
- Incluye: `docs/*.md` (son el entregable público), `docs/readme-*.png` /
  imágenes referenciadas desde el README o el blog, código fuente del motor y
  del dashboard, tests.
- Revisa el mensaje de commit: autor correcto (`rcm-on`, nunca atribución de
  IA — ver `feedback-no-ai-commit-attribution` en memoria), sin
  `Co-Authored-By: Claude` ni similar.

---

## 11. Qué hacer cuando un fallo es real (no ambiental)

Descartado que sea uno de los falsos fallos de §2/§11-siguiente, trátalo así:

1. **Aísla el caso mínimo.** No reportes "falla el corpus X" — reduce a la
   sentencia SQL concreta que dispara el fallo (como hacen los tests nuevos de
   cada arreglo en `tests/TSqlParser.Tests/`: un `CREATE PROCEDURE` de 10-20
   líneas que reproduce el síntoma, no el procedimiento de producción entero).
2. **Comprueba si es un falso negativo o un falso positivo.** Un falso
   negativo silencioso (el grafo dice "no hay riesgo" cuando sí lo hay, o
   "nadie escribe aquí" cuando alguien sí escribe) es la categoría más grave de
   este proyecto — súbelo de prioridad automáticamente.
3. **Antes de arreglar, congela.** Anota las cifras agregadas de los 4 corpus
   *antes* del arreglo (nodos/aristas/tests), igual que en
   `docs/corpus-multibase.md`. Sin línea base no hay forma de atribuir qué
   movió qué.
4. **Arregla, luego repite toda la guía**, no solo el paso que fallaba — un
   arreglo en `AstWalker`/`GraphExporter` puede mover corpus que no tocabas a
   propósito (pasó varias veces esta sesión).
5. **Documenta el hallazgo** en `docs/corpus-multibase.md` (o el doc de
   ejecución vigente) con: dónde estaba, por qué es grave, la hipótesis de
   causa raíz **verificada** (no solo la primera que se te ocurra — en esta
   sesión más de una hipótesis inicial resultó falsa al comprobarla), y qué se
   deja fuera a propósito si el arreglo tiene alcance limitado.
6. **No integres sin pruebas nuevas** que fallen antes del arreglo y pasen
   después — es la única prueba de que el arreglo hace algo.

---

## 12. Trampas del entorno conocidas (no son bugs de código)

| Síntoma | Causa | Qué hacer |
|---|---|---|
| `FileLoadException ... 0x800711C7` en `dotnet test`/`run` | Smart App Control bloqueando un `.dll` sin reputación | Ver §2. Reintentar 1-2 veces; si persiste, documentarlo como bloqueo ambiental, no como fallo |
| Un artefacto sale truncado tras `... \| Select-Object -First N` en PowerShell | `-First N` mata el proceso aguas arriba antes de que termine de escribir | Redirige a fichero (`> out.txt`) y lee el fichero, no uses `-First` sobre un pipeline que escribe a disco |
| `sqlcmd` falla con combinaciones de flags | `-h` y `-y 0` son incompatibles; `-W` e `-y` también | Usa `-C -h -1 -W` |
| Rutas de compilación "too long" en el scratchpad | El scratchpad tiene una ruta muy larga para el compilador de .NET | Compila en `C:\temp\<algo-corto>`, no en el scratchpad |
| `git worktree add` no trae `out/` | `out/` no está trackeado en el commit de esa rama | Genera los artefactos de nuevo tras crear el worktree, no esperes que vengan |

---

## 13. Checklist resumen (para copiar y marcar)

```
[ ] dotnet build TSqlLineageToolkit.slnx           → 0 Errores
[ ] dotnet test TSqlLineageToolkit.slnx            → 0 Con error (o solo el falso fallo de SAC, ver §2)
[ ] 4 corpus (WWI, AW2019, Ola, FRK)                → 0 parse errors, cifras explicadas
[ ] validate (WWI y AW2019)                        → 0 ausencias, 0 fantasmas en ambos sentidos
[ ] Caso de control (objeto conocido sin tocar)     → cifra exacta esperada, sin marcas de inferencia nuevas
[ ] unknown_edge_types / unknown_labels / orphan_edges → vacíos
[ ] Los 3 formatos de salida (.json/.db/.nodestore) → mismos nodos/aristas
[ ] Capturas del dashboard revisadas a ojo          → sin huecos en blanco, operadores completos
[ ] AST vs. grep en el objeto más complejo tocado   → AST cuenta menos, diferencia explicada
[ ] Agente + JSON vs. dashboard vs. sqlcmd           → las tres coinciden
[ ] git status revisado antes de add/commit         → solo lo que corresponde, autor correcto
```
