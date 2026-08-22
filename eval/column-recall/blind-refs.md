# Referencias de columna ciegas — clasificación por causa (medición, no arreglo)

Fecha de medición: **2026-08-22**. Solo mide; no se ha tocado `src/**` del motor ni ningún
test existente. Sustituye a la clasificación de 2026-08-16, hecha cuando había 140 ciegas:
tras los arreglos de la sesión del 18/19 de agosto **esa clasificación ya no describe lo
que queda**, que es justo el motivo de la regla de reclasificar antes de seguir arreglando.

## Comando de reproducción

Ya no hace falta un programa de un solo uso: el listado sale del subcomando `blind-refs`.

```bash
dotnet build ParserGeneral.sln -c Release
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll blind-refs dnn ciegas.csv
```

El agregado lo sigue gateando la suite:

```bash
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ColumnRecallGateTests"
```

## DNN Platform — resultado agregado

```
catálogo (bruto)         = 7786
catálogo laxo (mod,col)  = 7302
grafo laxo (mod,col)     = 7848
recall laxo              = 99,6987 %
BLIND (laxo)             = 22
```

Evolución: **140** (2026-08-16) → **90** (2026-08-19) → **22** (hoy).

## Cómo se ha clasificado esta vez

La clasificación anterior se hizo leyendo el SQL y deduciendo la causa. Falló dos veces
(la causa «otras, sin aislar» y la atribución equivocada de `registerassembly`). Esta vez
cada causa está **confirmada por sonda ejecutada**: un fichero `.sql` mínimo con el patrón
sospechoso, pasado por `from-sql` + `--columns`, comparando qué aristas de columna salen
frente a la misma consulta sin el patrón. Una sonda que no reproduce el ciego invalida la
hipótesis, y así se cayó una: los `major/minor/build` de `registerassembly` **no** son la
TVF, son el `ORDER BY`.

## Clasificación por causa raíz (22 de 22, sin residuo)

| # | Causa | Ciegas | Confirmada por |
|---|---|---:|---|
| A | **Lista `SELECT` de una expresión `UNION`**: las columnas del `SELECT` no se resuelven en ninguna rama | 5 | `ProbeUnion` vs `ProbeNoUnion` |
| B | **Columna solo en `ORDER BY`**, nunca en `SELECT`/`WHERE`/`ON` | 6 | `ProbeOrderByOnly`, `ProbeTvfOrderBy` |
| C | **TVF como origen de filas dentro de un scope anidado** (tabla derivada o `EXISTS`): sus columnas de salida no entran en ese scope | 4 | `ProbeTvfInDerived`, `ProbeTvfJoinOnInExists` |
| D | **Cuerpo de una CTE consumida por `DELETE ... FROM cte`** | 2 | `ProbeCteDelete` |
| E | **`SELECT *` dentro del `USING (...)` de un `MERGE`**: no se expande | 2 | `ProbeMergeUsingStar` |
| F | **Join anidado entre paréntesis**: se pierde su `ON` interior **y** las referencias del `ON` exterior a tablas de dentro | 1 | `ProbeParenJoin`, `dbo.JoinEntreParentesis` |
| G | **`INSERT INTO @tablavar SELECT * FROM <tvf>`**: el `*` no se expande | 1 | `ProbeInsertTvVarSelectStar` |
| H | **`UPDATE ... SET @variable = columna`** | 1 | `ProbeUpdateSetVar` |
| | **Total** | **22** | |

### Reparto por (módulo, columna)

| Causa | Módulo | Columna |
|---|---|---|
| A | `dbo.getextensionurlproviders` | `tabid` |
| A | `dbo.gettabcustomaliases` | `httpalias` |
| A | `dbo.vw_contentworkflowusage` | `content`, `folderpath`, `metadatavalue` |
| B | `dbo.getavailableusersforindex` | `vieworder` |
| B | `dbo.getprofilefieldsql` | `vieworder` |
| B | `dbo.gettaburls` | `taborder` |
| B | `dbo.registerassembly` | `major`, `minor`, `build` |
| C | `dbo.journal_get` | `seckey` |
| C | `dbo.journal_listforgroup` | `seckey` |
| C | `dbo.journal_listforprofile` | `seckey` |
| C | `dbo.journal_listforsummary` | `seckey` |
| D | `dbo.purgeeventlog` | `logconfigid`, `logcreatedate` |
| E | `dbo.ensurelocalizationexists` | `issecure`, `portalsettingid` |
| F | `dbo.getinstalledmodules` | `moduledefid` |
| G | `dbo.getusersadvancedsearch` | `rownumber` |
| H | `dbo.addfile` | `fileid` |

### Sondas, con el resultado tal cual salió

**A — `UNION`.** La misma consulta, con y sin `UNION`:

```sql
-- ProbeUnion:   SELECT t.TabId, trp.CultureCode, pa.HttpAlias FROM ... UNION SELECT (idem)
-- ProbeNoUnion: SELECT t.TabId, trp.CultureCode, pa.HttpAlias FROM ...
```

| | `READS_FROM` | `FILTERS_ON` | `READS_COLUMN` |
|---|---:|---:|---:|
| `ProbeNoUnion` | 3 | 4 | **3** |
| `ProbeUnion` | 6 | 8 | **0** |

Con `UNION` las tablas y los predicados salen enteros y **la lista `SELECT` no aporta ni
una arista**. Es un defecto general, no de estas cinco filas: solo produce 5 ciegas porque
la mayoría de columnas de una consulta con `UNION` aparecen además en algún `ON` o `WHERE`,
que sí se recorre. Las cinco que quedan son las que viven **únicamente** en el `SELECT`.

**B — `ORDER BY`.** `SELECT dm.ModuleName FROM dbo.DesktopModules dm ORDER BY dm.IsAdmin`
resuelve `ModuleName` y no `IsAdmin`. Ojo: esta es la causa que **B3 del plan ya intentó
arreglar una vez y tumbó la precisión de la clase `direct`** (el intento está en
`stash@{0}`). El listón para volver a tocarla sigue alto.

**C — TVF en scope anidado.** `t.seckey` se resuelve en un `JOIN` llano y se pierde en
cuanto ese mismo `JOIN` vive dentro de una tabla derivada o de un `EXISTS`:

```
ProbeTvfJoinOn          -> FILTERS_ON journal_user_permissions.seckey   ✓
ProbeTvfJoinOnInExists  -> (ninguna arista a la TVF)                    ✗
ProbeTvfInDerived       -> (ninguna arista a la TVF)                    ✗
```

Las cuatro ciegas de `seckey` son el mismo mecanismo: `journal_get` lo tiene bajo
`NOT EXISTS`, los tres `journal_listfor*` bajo una tabla derivada.

**D — CTE consumida por `DELETE`.** De `ProbeCteDelete` solo salen las columnas de
`EventLogConfig` (la tabla del `JOIN` externo); `LogConfigID` y `LogCreateDate`, que viven
en el cuerpo de la CTE, no aparecen.

**E — `SELECT *` en `MERGE ... USING`.** `ProbeMergeUsingStar` devuelve solo `PortalID`,
que viene del `WHERE`. Las columnas que solo aporta el `*` (`PortalSettingID`, `IsSecure`)
se pierden. Contraste útil: `SELECT * FROM tvf()` a secas **sí** se expande
(`ProbeTvfStar` resolvió las tres), así que el fallo está en el `USING`, no en el `*`.

**F — Join anidado entre paréntesis.**

```sql
LEFT JOIN (dbo.[ModuleDefinitions] MDEF INNER JOIN dbo.[Modules] MODS ON MDEF.ModuleDefID = MODS.ModuleDefID)
ON dm.DesktopModuleID = MDEF.DesktopModuleID
```

El `ON` de fuera se recorre, pero **solo para la tabla que está fuera del paréntesis**: al
gatear este patrón salió que `h.MadreId`, que vive en el `ON` exterior, también se pierde
por apuntar a una tabla de dentro. Eso no lo había visto leyendo el SQL; lo dijo el gate.

**G — `INSERT INTO @tablavar SELECT * FROM <tvf>`.** `ProbeInsertTvVarSelectStar` no
produce **ninguna** arista de columna, mientras que el mismo `SELECT * FROM tvf()` suelto
resuelve las tres. El destino de tabla-variable mata la expansión.

**H — `UPDATE ... SET @variable = columna`.** `ProbeUpdateSetVar` emite `WRITES_TO`,
`WRITES_COLUMN` sobre `FileName` y `FILTERS_ON` sobre `FolderID`, pero la lectura de
`FileId` en `@FileID = FileId` no se registra.

## Gateado, no solo documentado

Cinco de las ocho causas están ahora en `eval/blind-patterns/` con estado declarado, así que
la suite las hace cumplir en los dos sentidos: si una ciega se arregla sin actualizar el
ground-truth, el gate falla pidiendo la actualización; si una cubierta se rompe, falla como
regresión. Ambas direcciones se han visto fallar a propósito.

| Patrón | Entradas nuevas | Ciegas declaradas | Controles cubiertos |
|---|---|---:|---:|
| `union-select-list` | `dbo.UnionSelectList`, `dbo.UnionSinUnion` | 1 | 5 |
| `tvf-scope-anidado` | `dbo.TvfEnDerivada`, `dbo.TvfEnJoinLlano` | 1 | 4 |
| `cte-delete` | `dbo.CteBorradaPorDelete` | 2 | 2 |
| `join-entre-parentesis` | `dbo.JoinEntreParentesis` | 3 | 1 |
| `update-set-variable` | `dbo.UpdateSetVariable` | 1 | 2 |

Los controles importan tanto como las ciegas: `dbo.UnionSinUnion` y `dbo.TvfEnJoinLlano` son
**controles negativos** — la misma consulta sin el patrón sospechoso, que sí resuelve. Sin
ellos, «el `UNION` rompe las columnas» sería una correlación, no un diagnóstico.

Las causas **B (`ORDER BY`)**, **E (`SELECT *` en `MERGE ... USING`)** y **G
(`INSERT INTO @tablavar SELECT *`)** no entran en este gate: B ya estaba (patrón `order-by`,
3 ciegas declaradas), y E y G necesitan esquema real de tabla (`CREATE TABLE`) para que
haya un `*` que expandir, que es justo lo que el corpus de `blind-patterns` no lleva. Quedan
documentadas y sin gatear, dicho explícitamente en vez de omitido.

## Comprobaciones de instrumento (regla del cero culpable)

- Las 22 filas del CSV suman exactamente 22 en la tabla de causas: sin residuo, sin
  duplicados, sin categoría «otras». La causa «otras» de la clasificación anterior era
  precisamente la señal de que faltaba diagnóstico, y esta vez se cerró (era A).
- Ninguna causa salió en 0. La más pequeña (F, G, H) tiene 1 caso cada una, y cada uno
  tiene sonda propia que lo reproduce.
- **Una hipótesis se cayó por la sonda**, que es la prueba de que el instrumento discrimina:
  `registerassembly.major/minor/build` parecía TVF (`CROSS APPLY dbo.fn_ParseVersion`) y es
  `ORDER BY` — `ProbeTvfWhere` demostró que las columnas de una TVF **sí** se resuelven en
  un `WHERE`, así que la TVF no era el bloqueo.
- El recall medido (99,6987 %) sale del subcomando oficial `blind-refs`, el mismo binario
  de Release que corre la suite, no de un programa ad hoc.
- Las cinco causas gateadas se confirmaron **dos veces por caminos distintos**: las sondas
  van por `from-sql` → `InputAnalyzer` → `GraphExporter`, y el gate por
  `SqlAnalyzer.AnalyzeObject` → `GraphExporter` → `SqliteExporter`. Coinciden en las cinco.
- El ground-truth del gate no se escribió a mano: se declaró todo «ciego» a propósito y se
  dejó que el gate dijera cuáles no lo eran. De las 22 filas sondeadas, 14 salieron cubiertas
  — incluidos los dos controles negativos.

## Qué atacar primero

Por relación entre ciegas y localización del arreglo:

1. **A (`UNION`)** — 5 ciegas, pero es el único de la lista que es un **defecto general del
   motor**, no un caso de borde: hoy ninguna consulta con `UNION` aporta columnas de su
   lista `SELECT` en todo el corpus. El impacto real es mayor que 5 y el corpus no lo mide,
   porque lo tapa la redundancia con `ON`/`WHERE`. Es también el más barato de gatear.
2. **C (TVF en scope anidado)** — 4 ciegas y un punto de convergencia claro: el scope
   anidado no hereda las columnas de salida de la TVF. Sospecha razonable de que comparte
   mecanismo con el arreglo de subconsultas de la sesión anterior.
3. **E, F, G** — 4 ciegas entre las tres, cada una un caso acotado de un tipo de sentencia.
4. **B (`ORDER BY`)** — 6 ciegas, las más de todas, y aun así **la última**: ya tumbó la
   precisión una vez. No entra sin un gate de precisión por clase delante.

## WWI-DW — pendiente de reclasificar

El conteo de 2026-08-16 (55 ciegas, 84,89 % de recall) es **anterior a los arreglos** y no
se ha vuelto a medir. No usarlo como diagnóstico: por la misma razón que obligó a reescribir
este documento, esas 55 ya no son las mismas.
