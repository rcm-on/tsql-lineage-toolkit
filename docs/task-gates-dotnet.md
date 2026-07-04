# Tarea: gates de evaluación en .NET (eliminar run.mjs del camino crítico)

**Decisión (Ramón, 2026-07-03):** los gates de validación del analizador deben ser
.NET — `dotnet test` como gate único. Node queda solo para el e2e Playwright del
dashboard (otra superficie); el oráculo sqlglot (Python vía `uv`) queda como oráculo
manual opcional, fuera del gate.

**Motivación (auditoría 2026-07-03, medida, no narrada):**

- `eval/community-edge-cases/run.mjs` **no verifica nada**: solo `exit(1)` si el
  pipeline crashea. Los conteos "merge=3, union=2…" de la bitácora fueron
  verificación manual → un refactor que deje MERGE en `DERIVES_FROM=0` pasaría en
  silencio. Es un smoke test disfrazado de gate.
- La suite unitaria corre (96/97) pero tiene **1 test flaky por orden**:
  `Update_MatchesFreshWrite_ExceptGeneratedAt` (`NodeStoreUpdateTests.cs:442`)
  falla en suite completa y pasa en aislamiento → fuga de estado entre tests.
- El gate más fuerte (`eval/view-lineage/crosscheck.mjs`) depende de
  `localhost\SQLEXPRESS` → no portable a CI sin contenedor.
- 4 features constan en `docs/coverage-matrix.md` como "validado por corpus, **sin
  test unitario**": MERGE OUTPUT, UNION en vista, CTE recursiva, BusinessRule.

**Anti-objetivo:** portar `run.mjs` a C# tal cual (script que spawnea el DLL sin
aserciones) sería el mismo error en otro lenguaje. El diseño correcto es corpus
como datos + xUnit data-driven **in-process**.

---

## Pasos (cada uno deja la suite en verde antes del siguiente)

### 0. Arreglar el test flaky — CERRADO (2026-07-03)

**Causa raíz (demostrada, no supuesta):** no era orden de tests ni estado
compartido — era **tiempo de reloj**. `audit_report.json` (añadido en `49fef08`,
posterior al test) lleva `generated_at` con precisión de segundos
(`AuditExporter.cs:513`); `Update_MatchesFreshWrite_ExceptGeneratedAt` normaliza
el timestamp de `index.json` pero comparaba `audit_report.json` **byte a byte**,
así que fallaba cuando `Update(storeA)` y `Write(storeB)` caían en segundos
distintos (~25% por ejecución; en aislamiento el test dura ~260 ms y casi nunca
cruza). Demostración: mismo input procesado 2 veces con 1,5 s entre medias →
`diff -rq` de los nodestores completos = **solo** `generated_at` en
`audit_report.json` e `index.json`. Entró sin detectarse porque la suite no
corría por SAC.

**Fix (test-side; el timestamp es feature legítima, mismo precedente que
index.json):** helper `NormalizedAuditJson` + comparación normalizada en los
**2** tests afectados — `Update_MatchesFreshWrite_ExceptGeneratedAt` y
`Update_NoChange_LeavesAllFilesUnchanged` (este segundo tenía la misma bomba
latente y aún no había explotado). `Update_SingleObjectChange` compara solo
ficheros concretos: a salvo.

**Verificado:** suite completa 3/3 en verde (97/97) + clase
`NodeStoreUpdateTests` 10/10 ejecuciones sin fallo.

**Observación menor (no arreglada, anotar si se toca):** `index.json` usa
`DateTime.Now.ToString("o")` (hora local) y el audit `DateTimeOffset.UtcNow`
con sufijo `Z` — formatos y zonas inconsistentes entre sí.

### 1. Migrar `eval/bad-practices` — RE-ALCANZADO (2026-07-03, ver hallazgo)

**⚠️ Hallazgo que invalida el alcance original** (detectado por el agente
DeepSeek en el experimento de `agent-experiment-paso1.md` y **verificado**
contra `evaluate.mjs:32-51`): las reglas de detección de malas prácticas NO
están en C# — `evaluate.mjs` carga `dashboard/src/{naturalize,shape,risks}.js`
en un `window` simulado y ejecuta **el motor de reglas del dashboard**. El
"portar el comparador (~50 líneas)" era irrealizable: sin reglas en .NET no hay
hallazgos que comparar. `AuditExporter` existe pero emite otros patrones
(`risk_patterns`/`blind_spots`), no los 38 hallazgos del ground-truth.

**Decisión de diseño requerida (recomendación: opción A):**

- **A (recomendada): promover el motor de reglas a C#** — nuevo
  `RiskAnalyzer` en `src/TSqlParser/` que reimplemente las reglas de
  `risks.js` (+la parte de `shape.js` que consumen) sobre `GraphPayload`,
  emitiendo hallazgos `{component, rule, sev, cat}`. El test xUnit compara
  contra `expected-findings.json`. Única fuente de verdad en el motor; a
  futuro el dashboard y `evaluate.mjs` consumen la salida C# (o se deprecan).
  Alinea con .NET-first y con agent-first (hallazgos disponibles para
  agentes/MCP sin dashboard). Coste: medio (risks.js son ~138 líneas + subset
  de shape.js). **Es trabajo de motor: modelo fuerte, no NIM free.**
- **B (descartada): portar risks.js solo al proyecto de tests** — crearía una
  TERCERA copia de las reglas (dashboard JS + copia en tests) con divergencia
  garantizada. El gate pasaría la prueba de mutación pero validaría una copia,
  no el producto.
- **C (interina): el gate bad-practices se queda en Node** como excepción
  documentada a la regla .NET hasta que A esté hecha. El resto de pasos (2-4)
  no dependen de esta decisión.

- **Salida (con A):** `RiskAnalyzer` en C# + test xUnit en verde equivalente a
  `PASA OK=38 FALTAN=0 SOBRAN=0`, validado por prueba de mutación;
  `evaluate.mjs`/`run.sh`/`run.ps1` deprecados cuando el dashboard consuma (o
  se decida que siga con su copia JS, anotando el riesgo de divergencia).

**CERRADO (2026-07-03) — opción A implementada y verificada:**

1. **`src/TSqlParser/RiskAnalyzer.cs`** (nuevo): port fiel de las reglas de
   `risks.js` + el subset de `shape.js` que consumen, sobre `GraphPayload`.
   Tolera grafo in-process y deserializado (JsonElement). Detalles críticos
   preservados: `nullable = is_nullable !== false` (tabla materializada sin DDL
   = totalmente anulable), writers dedup por `op+objeto` vs tally bruto para
   escrituras repetidas, `usedBy` filtrado a steps del propio objeto,
   `complexity 0→1`, encadenado else-if de cursor/tx y de complejidad/anidación.
2. **`src/TSqlParser/InputAnalyzer.cs`** (extraído de `Program.cs`, refactor
   puro): el routing CREATE TABLE vs objetos + StripLeadingComments ahora es
   público — el gate usa EXACTAMENTE el camino de producción, sin copia.
3. **`tests/TSqlParser.Tests/BadPracticesGateTests.cs`**: pipeline in-process
   (`SqlFileLoader` → `InputAnalyzer` → `GraphExporter --columns` →
   `RiskAnalyzer`) contra `expected-findings.json`, reportando
   FALTA/SOBRA/SEV-CAT por componente (equivalente xUnit de `evaluate.mjs`).

**Verificación (ejecutada, no narrada):** gate xUnit OK=38/0 discrepancias a la
primera; **prueba de mutación** (crit→high en el oráculo) → falla con
diagnóstico exacto (`OK=37, SEV/CAT dbo.usp_SearchCustomers_Injection`) y
oráculo restaurado; suite completa **98/98 ×3**; gate Node original re-ejecutado
(`run.sh`): `PASA OK=38` — el refactor de Program.cs no cambió el CLI y ambos
motores (JS y C#) coinciden sobre el mismo corpus.

**Nota de deprecación:** `evaluate.mjs`/`run.sh` NO se borran aún — reconvertidos
conceptualmente en **guardia de paridad JS↔C#** (mientras el dashboard conserve
su copia de reglas en `risks.js`, correr run.sh detecta divergencia entre ambos
motores). Borrarlos solo cuando el dashboard consuma la salida C#.

### 2. `eval/community-edge-cases` como `[Theory]` data-driven

- Los `.sql` se quedan donde están (son corpus legible, valioso como datos).
- Junto a cada caso, un `expected.json` con **aristas concretas esperadas**
  (p. ej. `DERIVES_FROM: TargetProducts.Price ← SourceProducts.Price`), no solo
  conteos — los conteos se cumplen por accidente, las aristas concretas no.
  Valores de referencia ya medidos: merge=3, merge-with-output=5,
  recursive-cte=3, window=6, union-view=2, lineage-chain=9 (detallarlos a arista
  concreta al crear cada `expected.json`).
- Un `[Theory]` + `MemberData` que enumera los casos del corpus y ejecuta el
  pipeline in-process. Casos multi-fichero (lineage-chain) soportados.
- **Bonus:** esto fija como test unitario las 4 deudas de la matriz de cobertura
  (MERGE OUTPUT, UNION en vista, CTE recursiva, BusinessRule) sin escribir tests
  a mano. Actualizar `docs/coverage-matrix.md` (🟡 → ✅) al cerrar.
- **Salida:** `run.mjs` deprecado; mutar a propósito un expected y comprobar que
  el test FALLA (prueba del gate, no solo del caso feliz).

**CERRADO (2026-07-03) — verificado, no narrado:**

1. **`expected.json` por caso** (7, no 6 — se añadió `dynamic-sql-complex` porque
   ya estaba en `run.mjs`/en la lista de lectura de la tarea, aunque no traía
   total de saneamiento): `dml-advanced/merge.expected.json`,
   `dml-advanced/merge-with-output.expected.json`,
   `cte-recursive/recursive-cte.expected.json`, `window-functions/window.expected.json`,
   `set-ops/union-view.expected.json`, `lineage-chain/lineage-chain.expected.json`
   (multi-fichero, 4 `.sql`), `dynamic-sql/dynamic-sql-complex.expected.json`.
   Derivados ejecutando el pipeline in-process real (`SqlFileLoader.Run` →
   `InputAnalyzer.Analyze` → `GraphExporter.Build(includeColumns:true)`) desde un
   proyecto de exploración desechable (fuera del repo, borrado al terminar) que
   volcó las aristas `DERIVES_FROM`/`READS_FROM` reales. Los 6 totales de
   saneamiento del enunciado **cuadraron exactamente** contra la salida medida:
   `merge=3, merge-with-output=5, recursive-cte=3, window=6, union-view=2,
   lineage-chain=9` — ningún ajuste de expected para forzar el paso. Cada arista
   se guardó completa (`from`/`to`/`logic` para `DERIVES_FROM`; `source`/`target`/
   `properties` para `READS_FROM`), incluida una duplicación real en `merge`/
   `merge-with-output` (dos aristas idénticas `Price <- Price`, comportamiento
   existente del producto, no tocado) y el mapeo a "fantasma" de `Lvl` en
   `recursive-cte` (limitación conocida, documentada, no arreglada — invariante 1).
   `dynamic-sql-complex` midió `DERIVES_FROM=0 READS_FROM=1` (sin lineage de
   columna a través de SQL dinámico — gap conocido, no en el alcance de esta tarea).
2. **`tests/TSqlParser.Tests/CommunityEdgeCaseGateTests.cs`** (nuevo): `[Theory]`
   junto con `MemberData` que enumera los 7 casos (soporta multi-fichero), ejecuta el
   mismo pipeline in-process que el paso 1 (`SqlFileLoader` → `InputAnalyzer` →
   `GraphExporter --columns`) y compara `DERIVES_FROM`/`READS_FROM` como
   multiconjuntos contra el `expected.json` del caso, reportando `FALTA`/`SOBRA`
   por arista concreta (mismo estilo de mensaje que `BadPracticesGateTests`).
3. **Prueba de mutación** (parte del entregable): se cambió temporalmente
   `dml-advanced/merge.expected.json` (`logic: "s.Id"` → `"s.WRONG_MUTATION"`) y
   se corrió `dotnet test --filter FullyQualifiedName~CommunityEdgeCaseGateTests`.
   Salida real:

   ```text
   [FAIL] TSqlParser.Tests.CommunityEdgeCaseGateTests.Corpus_MatchesExpectedEdges(edgeCase: EdgeCase { Name = merge, ... })
   Mensaje de error:
      Gate community-edge-cases [merge]: DERIVES_FROM esperado=3 obtenido=3, READS_FROM esperado=1 obtenido=1, discrepancias=2
   FALTA DERIVES_FROM  CommunityCasesDB:table:dbo.targetproducts:column:Id <- CommunityCasesDB:table:dbo.sourceproducts:column:Id | s.WRONG_MUTATION
   SOBRA DERIVES_FROM  CommunityCasesDB:table:dbo.targetproducts:column:Id <- CommunityCasesDB:table:dbo.sourceproducts:column:Id | s.Id
   Con error! - Con error:     1, Superado:     6, Omitido:     0, Total:     7
   ```

   Mutación revertida; suite completa vuelta a verde antes de continuar.
4. **Suite completa:** `dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj
   -c Release` → `Con error: 0, Superado: 105, Omitido: 0, Total: 105` (98 previos
   más 7 casos nuevos de la `[Theory]`), repetido 3 veces sin fallo.
5. **`node eval/community-edge-cases/run.mjs`** re-ejecutado tal cual (sin tocar
   su lógica, solo nota de deprecación añadida al principio del fichero): procesó
   los 7 casos y terminó con `🎉 All community edge cases processed.` — sin
   regresión frente al comportamiento previo.
6. **Bonus (matriz de cobertura):** `docs/coverage-matrix.md` actualizado — MERGE
   `WHEN MATCHED`/`OUTPUT INTO`, UNION en vista y CTE recursiva pasan de 🟡
   "validado por corpus, sin test unitario" a ✅ "fijado por test 2026-07-03"
   (constraints `:BusinessRule`, la 4ª deuda, queda fuera de este corpus y sigue
   pendiente).

**No tocado (invariantes respetadas):** `src/TSqlParser/` sin cambios; los `.sql`
del corpus sin cambios; `run.mjs` no borrado (solo comentario de deprecación);
tests/gates de los pasos 0-1 no modificados.

### 3. `eval/view-lineage/crosscheck` en C# con trait de oráculo

- Portar `crosscheck.mjs` a un test con `Microsoft.Data.SqlClient`, reutilizando
  la consulta de `extract-truth.sql` (`sys.columns` +
  `sys.dm_sql_referenced_entities`).
- Marcar `[Trait("Category","Oracle")]`: `dotnet test --filter Category!=Oracle`
  corre en cualquier máquina; el job de CI con contenedor
  `mcr.microsoft.com/mssql/server` + restore de WWI corre la suite completa.
- Connection string por variable de entorno (default `localhost\SQLEXPRESS`,
  auth Windows).
- **Salida:** gate WWI (14/14 · 12/12 · 6/6, 0 discrepancias) reproducido desde
  `dotnet test`.

**CERRADO (2026-07-04) — verificado, no narrado:**

1. **`tests/TSqlParser.Tests/ViewLineageOracleTests.cs`** (nuevo): combina
   `[Trait("Category","Oracle")]` con `[Theory]`/`MemberData` por base de datos
   (WideWorldImporters, AdventureWorks2019). Pipeline in-process real:
   `ObjectExtractor.Run` (extrae las vistas del `ground-truth.csv` vía
   `sys.sql_modules`) más `TableSchemaExtractor.RunAll` (DDL de tablas base) →
   `InputAnalyzer.Analyze` → `GraphExporter.Build(includeColumns:true)` — mismo
   camino que `extract --tables` más el pipeline principal, sin atajos.
   Servidor configurable por `TSQLPARSER_SQL_SERVER` (default
   `localhost\SQLEXPRESS`).
2. **Hallazgo al portar:** `crosscheck.mjs` lee `edges_out` del nodestore (donde
   `NodeStoreExporter.OwnerOf` ya resuelve la propiedad de una arista al
   `SqlObject` a través del prefijo `<objId>#stepN`). El test C# no pasa por el
   nodestore — replica esa misma regla de propiedad directamente sobre
   `graph.Relationships` (helper `OwnedBy`, equivalente a `nodeId == objId` o
   `nodeId` empezando por `objId` seguido de `#`). Verificado a mano contra el
   grafo real de `Website.Customers` y `HumanResources.vEmployee` antes de fijar
   el test: out=14/18, src=24/30, tbl=6/9 — coincide exactamente con
   `ground-truth.csv` en ambos.
3. **El gap de columnas de salida descrito en `eval/view-lineage/README.md`
   (0/14 modeladas) ya no reproduce** — `HAS_COLUMN`/`BuildViewLineage` sí
   dispara hoy para las vistas reales de WWI; el README quedó desactualizado
   (no se toca en esta tarea, es doc de otro hallazgo cerrado en otro momento).
4. **Un gap nuevo, real y no introducido por esta tarea:**
   `AdventureWorks2019.Sales.vSalesPersonSalesByFiscalYears` usa `PIVOT` sobre
   una tabla derivada; `ViewColumnLineage` no la camina → mide out=0/src=0 en
   vez de 7/13. Documentado y excluido de la comparación estricta
   (`KnownGaps` en el test, con comentario) en vez de forzarlo o de tocar
   `src/TSqlParser` (invariante 1). El resto de AdventureWorks (19/20 vistas)
   y las 3 de WWI cuadran exactas.
5. **Prueba de mutación:** `ground-truth.csv` de `Website.Customers` cambiado
   temporalmente `out_cols 14→999`; `dotnet test --filter
   FullyQualifiedName~ViewLineageOracleTests` → falla con `out_cols
   esperado=999 obtenido=14`. Revertido; suite completa vuelta a verde.
6. **Verificado:** `dotnet test --filter Category!=Oracle` → 105/105 (esta
   clase no corre, tal como se espera sin marcar `Category`). `dotnet test`
   sin filtro (con SQL Server disponible) → 107/107, ~36 s (dominado por las 2
   llamadas Oracle: extracción en vivo de 3+20 vistas y sus tablas base).

**Extensión (2026-07-04) — `eval/auditor-challenge/verify.mjs` portado:**
`tests/TSqlParser.Tests/AuditorChallengeGateTests.cs` (nuevo, mismo trait
`Category=Oracle`) regenera el nodestore COMPLETO de WideWorldImporters
in-process (`ObjectExtractor.Run` sin filtro de objeto/like, para tener el
grafo de llamadas/escrituras entero, no solo un puñado de vistas) y verifica
las mismas 4 comprobaciones que `verify.mjs`: `cyclomatic_complexity==19` y
`unresolved_dynamic_sql_steps==0/34` de
`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`, sus 17 tablas
`WRITES_TO` exactas (vía `nav.json`) y el impacto en `lineage_path.json` de
`Website.Customers` (14/14), `Website.Suppliers` (12/12) y
`Website.VehicleTemperatures` (0/6, caso negativo). Prueba de mutación
(`cyclomatic_complexity` esperado 19→999) → falla con
`cyclomatic_complexity esperado=999 (snapshot WWI) obtenido=19`; revertida.
`dotnet test --filter Category=Oracle` → 3/3 (los 2 de `ViewLineageOracleTests`
más este). `dotnet test --filter Category!=Oracle` → 113/113, ninguno de esta
clase ejecutado. Paridad JS↔C# verificada: `node
eval/auditor-challenge/verify.mjs` corrido a mano contra un nodestore WWI
regenerado con el CLI (`extract --tables` + `--columns --nodestore`) imprime
`TODOS OK` con los mismos números exactos que el test C# midió. `verify.mjs`
no se borra ni se modifica en su lógica (solo nota de deprecación al
principio, mismo patrón que `eval/community-edge-cases/run.mjs`).

### 4. CI (GitHub Actions)

- Job rápido en cada push: build + `dotnet test --filter Category!=Oracle`
  (unit + corpus + bad-practices).
- Job nightly/manual: contenedor SQL Server + restore WWI + suite completa
  (incluye Oracle).
- De paso: bump `SQLitePCLRaw` (NU1903, severidad alta) para CI sin warnings de
  seguridad.
- **Salida:** badge verde en el repo; ningún gate depende de una máquina concreta.

**CERRADO (2026-07-04) — verificado, no narrado:**

1. **`.github/workflows/ci.yml`** (nuevo): job `test` en `push`/`pull_request` a
   `master`, `runs-on: windows-latest` (mismo SO que la verificación local, sin
   sorpresas de rutas), `actions/setup-dotnet@v4` con `.NET 10`,
   `dotnet build src/TSqlParser/TSqlParser.csproj -c Release` seguido de
   `dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release
   --filter "Category!=Oracle"`. Cualquier test en rojo hace fallar el step (y
   el workflow) por comportamiento estándar de `dotnet test`.
2. **NU1903 eliminado.** Causa raíz: `TSqlParser.csproj` fijaba
   `Microsoft.Data.Sqlite 9.0.0`, que arrastra `SQLitePCLRaw.lib.e_sqlite3
   2.1.10` (vulnerabilidad alta, `GHSA-2m69-gcr7-jv3q` / `CVE-2025-6965`,
   corrupción de memoria en SQLite &lt;3.50.2). Bump a `Microsoft.Data.Sqlite
   10.0.9` (la serie alineada con `net10.0`) solo movió la transitiva a
   `SQLitePCLRaw.lib.e_sqlite3 2.1.11` — **el aviso de GitHub cubre `<= 2.1.11`
   con `first_patched_version: null`** (verificado contra
   `api.github.com/advisories/GHSA-2m69-gcr7-jv3q`): no existe una versión de
   ese paquete concreto que lo resuelva. El propio proyecto SQLitePCL.raw lo
   solucionó **renombrando** la dependencia nativa: `SQLitePCLRaw.bundle_e_sqlite3
   3.0.3` ya no depende de `SQLitePCLRaw.lib.e_sqlite3` sino de
   `SQLitePCLRaw.config.e_sqlite3` + `SourceGear.sqlite3 3.50.4.5` (SQLite
   3.50.4, por encima del umbral del CVE). Fix: referencia directa añadida en
   `src/TSqlParser/TSqlParser.csproj`:

   ```xml
   <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
   <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="3.0.3" />
   ```

   NuGet resuelve el diamante a la versión más alta sin conflicto (misma
   superficie de API `SQLitePCL.Batteries.Init()` que ya usa
   `Microsoft.Data.Sqlite`).
3. **Verificación (comandos reales, salidas reales):**

   ```text
   $ dotnet build src/TSqlParser/TSqlParser.csproj -c Release
     TSqlParser -> ...\bin\Release\net10.0\TSqlParser.dll
   Compilación correcta.
       0 Advertencia(s)
       0 Errores

   $ dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=Oracle"
   Correctas! - Con error:     0, Superado:   105, Omitido:     0, Total:   105
   ```

   (Los warnings `CS8600`/`xUnit2020`/`xUnit1026` que aparecen en la
   compilación de tests son preexistentes en el proyecto de tests — sin
   relación con NU1903 — y quedan fuera del alcance de esta tarea.)
4. **Paso 3 opcional (contenedor SQL Server + Oracle): NO implementado —
   bloqueo real, documentado como TODO en `ci.yml`, no como pereza.**
   Investigado antes de escribir el job: los tres extractores que usan SQL
   Server (`DbValidator.cs:129`, `ObjectExtractor.cs:76`,
   `TableSchemaExtractor.cs:193`) fijan `Integrated Security=true` (auth
   Windows) en la cadena de conexión, hardcoded — tocar eso para aceptar auth
   SQL violaría la invariante 1 (cero cambios de comportamiento en
   `src/TSqlParser/`). `mcr.microsoft.com/mssql/server` (única imagen oficial
   viable en un runner hospedado de GitHub Actions) es Linux y solo soporta
   auth SQL, no Integrated Security — no hay AD/Kerberos disponible en un
   runner efímero para hacerlo funcionar. Un runner `windows-latest` tampoco
   trae SQL Server preinstalado, y restaurar WWI + AdventureWorks2019 desde
   backups públicos con rutas casando con `RESTORE DATABASE` es, en sí mismo,
   una tarea separada de tamaño no trivial. El job `oracle-tests` queda en
   `ci.yml` con `if: false` y un comentario largo explicando el bloqueo exacto,
   para que quien retome esto no repita la investigación.
5. **Badge verde:** no confirmable desde aquí — depende de que Ramón haga push
   y GitHub Actions ejecute el workflow. La verificación local equivalente
   (mismo comando `dotnet test` del yml, ejecutado tal cual) es el punto 3
   anterior: 105/105.

**No tocado (invariantes respetadas):** `src/TSqlParser/*.cs` sin cambios de
comportamiento (solo el `.csproj` para el bump de paquetes); tests existentes
sin cambios; `eval/` sin tocar; ningún gate Node borrado.

---

## Invariantes (no negociables durante la migración)

1. **Cero cambios de comportamiento en `src/TSqlParser/`** salvo el fix del flaky
   (paso 0) si resulta ser bug de producto y no de test. Esta tarea es de
   infraestructura de calidad, no de extracción.
2. Ids de `:Column`/`:SqlObject`/`:Table` intactos (invariante 4-Q4 de
   `agent-collab.md`).
3. Ningún gate Node se borra hasta que su equivalente .NET haya fallado ante una
   mutación provocada (paso 2) o reproducido sus números (pasos 1 y 3).

## Relación con `change_map.json` (la prioridad anterior)

Esta tarea **desplaza** temporalmente a `change_map.json` y es deliberado:
`change_map` toca `NodeStoreExporter`/comparación de nodestores, exactamente la
zona del test flaky y de los gates débiles. Construir la red de regresión primero
hace `change_map` más barato y más seguro. `change_map` arranca al cerrar el
paso 2 (los pasos 3-4 pueden solaparse con él).
