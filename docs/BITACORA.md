---
title: Bitácora
description: Qué cambió en cada sesión, lo más reciente arriba.
read_when: Para saber qué pasó en las últimas sesiones antes de continuar el trabajo.
related: [docs/PROYECTO.md, docs/plan-arquitectura.md, docs/CONVENCIONES.md]
stability: volatile
updated: 2026-08-19
---

# Bitácora

Qué cambió en cada sesión, lo más reciente arriba. **Se escribe al terminar la sesión,
siempre**, aunque la sesión haya sido corta o haya salido mal — una sesión que no dejó
nada escrito es una sesión que hay que reconstruir leyendo commits.

Formato de una entrada: qué se hizo, qué se aprendió que no estaba previsto, en qué estado
queda el árbol y cuál es el siguiente paso concreto.

---

## 2026-08-19 (tarde) — Nueve herramientas MCP y diagnóstico de las 90 ciegas

**Estado al cortar**: `main` en `daaa5a3`. Publicado hasta `a4cb3eb`. **330/330** en la
última pasada estable (`e1594b8`). Hay un agente de `AstWalker` **en vuelo**: ver abajo.

### Herramientas MCP: de 2 a 9

`blind_spots`, `diff_impact` y `risks` sobre las seis anteriores.

`risks` era el bloqueante del informe de auditoría, y exigía una decisión: `RiskAnalyzer`
trabaja sobre `GraphPayload` y el MCP solo tiene SQLite. Se decidió **rehidratar**, no
reescribir las reglas en SQL (dos copias divergen). La cadena completa:

1. `SqliteExporter` guardaba solo `Labels[0]`; en el grafo real hay **108 nodos con dos
   labels**. Ahora `props` lleva la lista entera.
2. `GraphRehydrator` en `Parser.Graph`, al lado del exportador.
3. **Gate de fidelidad**, que es lo que lo convierte en hecho: la prueba fuerte es que
   `RiskAnalyzer` produce el **mismo conjunto exacto de hallazgos** sobre el grafo original
   y sobre el rehidratado.
4. `risks` con la procedencia de la evidencia en el contrato desde el primer día:
   `datos_de_ejecucion` detectado por las props de `PlanEnricher`, y advertencia explícita
   de que sin planes el orden es estructural.

`Parser.Mcp` pasa a referenciar `Parser.Graph` (por `ChangeMapDiff`). No invierte la
flecha. `docs/ARQUITECTURA.md` actualizado.

### Diagnóstico de las 90 ciegas — el hallazgo del día

**79 de 90 aparecen literalmente en el SQL**: no son casos exóticos, es que el walker no
recorre el constructo. Reproducido con 5 procedimientos mínimos:

| Patrón | Estado | Ciegas en DNN |
|---|---|---|
| Subconsulta escalar en la lista `SELECT` | **CIEGO** | 38 |
| `IF EXISTS(SELECT ...)` | **CIEGO** | 12 |
| `ORDER BY col` | **CIEGO** | 6 |
| `OUTPUT inserted.col` | **CIEGO** | 1 |
| `IN (SELECT ...)` | funciona | — |

**57 de 79 explicadas por cuatro patrones.** Hipótesis confirmada: el walker entra en
subconsultas alcanzables desde DML, y ni la escalar de la lista `SELECT` ni la de
`IF EXISTS` (control de flujo) lo son.

Y el dato que justifica arreglarlo: las columnas ciegas están en **posiciones de filtro y
orden** (`WHERE h.Estado = 0`, `ORDER BY Creado`), no de proyección. Son las que al
cambiarlas alteran **qué filas devuelve** el procedimiento, en silencio. #leccion

Gate ya commiteado (`daaa5a3`), y su ground-truth empírico coincidió al 100% con el
diagnóstico a mano.

### HECHO: subcomando `recall` — el motor mide cuánto ve sobre la base del usuario

`recall <database> [--server X] [--out csv]`. Extrae los módulos de la base viva, los
analiza y contrasta contra `sys.dm_sql_referenced_entities` de esa misma base.

Devuelve **las tres cosas juntas, nunca una sola**: el porcentaje, la **lista nominal**
`módulo,columna` de lo que no ve —que es lo accionable—, y los objetos con SQL dinámico sin
resolver, que el catálogo tampoco ve. Publicar el porcentaje sin esa advertencia sería
media verdad.

Verificado end-to-end contra SQL Server **en contenedor** (ya no hay SQL local): base mínima
de 4 procedimientos → catálogo 6, el motor ve 5, recall 83,3333 %, y la única ciega es
`dbo.orderbycol.creado` — exactamente el patrón que hoy se descartó por precisión. **Que la
limitación conocida sea justo lo que la medición detecta es la prueba de que mide de
verdad.**

Receta de verificación, minutos en vez de la media hora del script de backups:

```bash
docker run -d --name recalltest -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 14333:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
# crear una base mínima con los casos que interesen
TSQL_SQL_USER=sa TSQL_SQL_PASSWORD=... dotnet ...TSqlParser.dll recall <db> --server localhost,14333
```

### SIGUIENTE: el SQL dinámico, y en este orden

El hueco que ni el motor ni el catálogo ven. Tres vías, y **el orden importa**: empezar por
la última gasta contexto adivinando lo que las dos primeras dan con certeza.

**1. Observar lo que se ejecutó** (determinista). `capture-plans` y `XePlanCaptor` ya
existen: una sentencia dinámica que corre deja un plan **con las tablas reales**. No es
conjetura, es observación. Recordar que la atribución solo sale con XE + `event_file`
(`nest_level`); Query Store devuelve `object_id=0`.

**2. Ampliar la resolución estática** (determinista). `QUOTENAME`, `NCHAR`, `COALESCE` y
`CASE` ya están. Cada función que se sepa evaluar es dinámico que deja de serlo para
siempre, sin coste por ejecución.

**3. Hipótesis de LLM**, solo para lo que sobreviva a 1 y 2.

Sobre el 3, la regla que lo hace admisible: **nunca como arista del grafo.** El motor vale
porque es determinista; una inferencia de LLM es no determinista y aquí además
**infalsificable**, porque el catálogo tampoco ve esa zona. Mezclarla con `direct` o
`via_view` destruiría las clases de confianza.

Entra como capa aparte con `confianza: hipótesis`, con la evidencia para confirmarla de un
vistazo (objeto, línea y las asignaciones de la variable que alimenta el `EXEC`, que ya
están en las aristas `BUILDS_SQL_FROM`). Mismo patrón que `risks` con `evidencia:
estructural` y `datos_de_ejecucion`.

Y se mide como todo lo demás: **cuántas hipótesis confirma un humano**. Sin esa cifra sería
otra media verdad, solo que más cara. #leccion

### SIGUIENTE TAREA, y es más importante que las 40 restantes

**El usuario final no tiene la referencia de contraste.** Nosotros medimos el recall contra DNN y WWI
porque tenemos su catálogo. Un cliente ejecuta el motor sobre su base y **no sabe si le
faltan 40 o 400**: el motor emite lo que ve y calla lo que no. Es un silencio en el
producto, no en el desarrollo — y más grave que las 40, porque las 40 ya las conocemos.

**Los ingredientes ya existen.** `eval/column-recall/extract-catalog.sql` extrae el
ground-truth de **cualquier base viva**, no solo del corpus. Falta cablearlo para el
usuario: un subcomando y su herramienta MCP que respondan

> «sobre TU base, el motor ve el 99,2 % de las referencias de columna; estas 37 no las ve,
> y son de estos objetos».

Tercera pata de la honestidad, junto a `store_info` (¿de cuándo es el grafo?) y
`blind_spots` (¿dónde no puedo afirmar nada?). **Ninguna herramienta de análisis publica su
propio recall sobre la base del cliente.**

**Límite que hay que publicar con la cifra, siempre:** el catálogo de SQL Server también es
ciego al SQL dinámico. Una columna que solo se toca dentro de un `EXEC(@sql)` sin resolver
no aparece ni en el catálogo ni en el grafo, así que **no cuenta como ciega**: ese hueco no
lo ve ninguna de las dos partes. Publicar el recall solo, sin `blind_spots`, sería la clase
de media verdad que este proyecto persigue. #leccion

### Reclasificación de las 40 (regla nueva aplicada)

| Categoría | Ciegas |
|---|---|
| Sin referencia literal | 11 (28 %) |
| Lista `SELECT` | 8 |
| `JOIN ON` | 6 |
| Alias de derivada/CTE | 5 |
| Otros | 4 |
| `ORDER BY` | 3 |
| `OVER()` ventana | 2 |
| `WHERE` | 1 |

Cambió el reparto respecto al diagnóstico sobre 89: **"sin referencia literal" pasa a ser
el grupo mayor** y sigue sin diagnosticar — probablemente `SELECT *` o el catálogo contando
algo distinto. Es lo primero que hay que mirar, y no lo era antes de reclasificar.

### RESULTADO: 90 → 40 ciegas, recall 98,7675 % → 99,4522 %

Cuatro cambios, precisión intacta en **todos** (`ColumnRecallGateTests` 8/8 en cada paso):

| # | Cambio | Ciegas | Commit |
|---|---|---|---|
| 1 | `OUTPUT` sin `INTO` | 90 → 89 | `7f5a42e` |
| 2 | Scope en la lista `SELECT` | 89 → 76 | `898cf73` |
| 3 | Scope en el predicado de `IF`/`WHILE` | 76 → 67 | `aed46ee` |
| 4 | **Consolidación en `EmitSubqueryReads`** | 67 → **40** | `babc05d` |

Y uno **descartado**: `ORDER BY` bajaba a 86 pero tumbaba la precisión. En `stash@{0}`.

#### El hallazgo que vale más que los números

El mecanismo estaba **entero, en dos mitades que nunca se encontraron**:

- `EmitSubqueryReads` sabía **qué tablas** toca una subconsulta anidada, y ya estaba
  cableado a `IF`, `WHILE` y cualquier sentencia. Pero llamaba a `BuildExtraReads` con una
  lista de columnas **vacía**.
- `ScopedColumnCollector` / `ResolveScopedColumns` sabían resolver **columnas en su propio
  scope**, pero solo vivían en `ExtractFilterColumnsCore`: solo `WHERE`.

Unirlas en el punto de convergencia cubrió de golpe lista `SELECT`, argumentos de función,
`RETURN` de función escalar, cuerpos de CTE y predicados de control de flujo. Los cambios
2 y 3 fueron parches que el 4 dejó en gran parte redundantes.

**Ese `new List<TableColumnRef>()` era un hueco silencioso**: no fallaba, no avisaba, no
dejaba un TODO. Registraba media verdad, y una lista vacía se lee igual que "aquí no hay
columnas". Es el mismo modo de fallo que el producto persigue —un cero que parece una
respuesta— reproducido dentro del propio motor. #leccion

#### Error de método, para no repetirlo

Diagnostiqué desde el **corpus** (qué patrones fallan) en vez de desde el **código** (qué
scopes reciben resolución de columnas). El corpus decía "cuatro patrones" y llevaba a
arreglarlos de uno en uno; el código decía "un punto de convergencia".

**Ante un fallo repetido en varios sitios, buscar el punto donde convergen antes de
arreglar el primero.** #leccion

#### Lo que queda de las 40

El reparto anterior ya **no describe** lo que hay: hay que reclasificar antes de tocar.
Pendientes conocidos: tabla derivada (~8, necesita mapeo alias → columna de salida, el
único diseño nuevo que falta), `ORDER BY` (~4, necesita detectar alias de salida), y ~11
sin referencia literal, sin diagnosticar.

### CAUSA RAÍZ de las ciegas: una sola, medida — léelo antes de tocar nada

No son cuatro patrones: son **cuatro síntomas de un mecanismo**. Medido sobre las 89:

| Causa | Ciegas | % |
|---|---|---|
| Solo scope anidado | 60 | 67 % |
| Ambas (scope + calificador no-tabla) | 8 | 9 % |
| Solo calificador no-tabla | 1 | 1 % |
| Sin referencia literal | 11 | 12 % |
| Ninguna de las dos | 9 | 10 % |

**69 de 89 (78 %) por un solo mecanismo.**

> El walker resuelve columnas contra una **lista plana de tablas del scope exterior**.
> Cuando una columna vive en un scope anidado —subconsulta escalar, `IF EXISTS`, tabla
> derivada, CTE— su propio `FROM` no está en esa lista, no casa con nada y se descarta por
> fallo cerrado.

Por eso `IN (SELECT)` sí funciona: se arregló como **caso particular**, no como mecanismo.
Seguir arreglando patrones de uno en uno es perseguir síntomas. #leccion

**Solución: pila de scopes.** Cada `QuerySpecification` apila un scope con su propio `FROM`
(alias → tabla real, o alias → mapa de columnas si es derivada/CTE); resolver una columna
es recorrer la pila de dentro hacia fuera; lo irresoluble se sigue descartando.

Dos razones por las que esto **no** repetirá la caída de precisión del intento con
`ORDER BY`:

1. Es **la regla de ámbito del propio T-SQL**, incluidas las subconsultas correlacionadas
   que ven el scope exterior. No adivina: implementa la especificación.
2. **Clasificación por construcción**: resuelta en el scope actual → `direct`; resuelta
   atravesando derivada o CTE → clase deducida, como ya hace `via_view`. Un error de
   resolución no puede contaminar la clase de máxima confianza.

Precedente en el código: `GraphExporter.viewBaseTables` ya hace el mapeo por columna a
través de una capa, y marca `via_view`. Falta lo mismo para derivadas y CTEs — hoy
`cteBaseTables` mapea a tablas base, **no por columna**.

**No cubre**: `ORDER BY` (7), que es alias de **salida** del SELECT, otro problema; ni las
11 sin referencia literal ni las 9 sin causa. Cobertura esperada: 69 de 89.

### Resultado negativo del primer intento — léelo antes de reintentar

El agente de `AstWalker` murió por cuota, pero dejó trabajo medible y **el veredicto es que
no sirve tal cual**. Guardado en `git stash@{0}`, no borrado.

| Métrica | Antes | Después |
|---|---|---|
| Ciegas (DNN) | 90 | **86** |
| Recall laxo | 98,7675 % | **98,8222 %** |
| Precisión `direct` y `star_expanded` | en verde | **CAE** |

Bajó el contador **emitiendo aristas de más**. Es justo el modo de fallo contra el que se
puso la regla en el encargo: *si un gate de precisión se pone en rojo, se acota la rama,
nunca se relaja el gate*. Un motor que inventa aristas no vale para firmar una migración,
que es exactamente lo que lo hace útil. #leccion

**Lo que sí quedó demostrado**: el arreglo de `ORDER BY` funciona, y **el gate nuevo lo
cazó solo**, fallando con *"`dbo.madre.creado` ya no es ciega: actualiza
expected-columns.json a cubierto"*. El instrumento cumplió en su primer uso real, en la
dirección menos evidente de las dos.

**Al reintentar**: el problema no es visitar `ORDER BY`, es **a qué tabla se atribuye la
columna** cuando hay varias en el `FROM` o alias de por medio. Empezar por acotar el scope,
y medir precisión *antes* que ciegas.

### EN VUELO al cortar (ya resuelto)

Un agente arreglando **`OUTPUT` y `ORDER BY`** en `AstWalker.cs` / `GraphExporter.cs`, con
prueba por mutación y midiendo `blind-refs dnn` antes y después. Si dejó cambios sin
commitear, **revisarlos antes de nada**: `git status`, y verificar que ningún gate de
precisión bajó. La regla que se le dio: si la precisión cae, se acota la rama, **nunca se
relaja el gate**.

### Aviso de entorno nuevo y serio

Un agente reportó **62 pruebas en rojo por `FileLoadException 0x800711C7` sobre
`Parser.Mcp.dll`** — Smart App Control. Tiene sentido: es un ensamblado **creado hoy**, sin
reputación. Yo corrí la suite en verde varias veces, así que es intermitente.

**Consecuencia práctica**: el criterio de terminado de cualquier agente puede dar **falso
rojo** en las 7 clases de pruebas de herramientas MCP. Hay que confirmarlo y, si persiste,
documentarlo en `docs/VERIFICACION.md`.

### Siguiente

1. Verificar/commitear lo que dejó el agente de `AstWalker`.
2. Confirmar si el bloqueo de SAC sobre `Parser.Mcp.dll` persiste.
3. **`IF EXISTS(SELECT...)`** (12 ciegas) y luego **subconsulta escalar en `SELECT`** (38).
   Van **solos**, nunca en paralelo: mismo fichero, y son los de ambigüedad de scope.
4. Rúbrica de severidades y reclasificación de las 22 reglas a **ISO/IEC 5055 + CWE**
   (5055 se construye sobre 138 CWE; no forzar CWE donde no lo haya).
5. Ampliar controles negativos de bad-practices: hoy son **2** de 24 componentes, y son lo
   único que mide falsos positivos.

**Decidido y no volver a discutir**: no competir en anchura de reglas de estilo (tsqllint
tiene 28 y es gratis; SQL Enlight 260+ y es comercial y bueno). Crecer solo en reglas que
exijan el grafo. `agent-context-kit` queda aparcado en su repo.

---

## 2026-08-19 (T19 + info) — El MCP cierra el bucle: seis herramientas

**Estado**: `main`, árbol limpio. **297/297** y **43/43**. Publicado.

Dos tareas en paralelo, con rutas disjuntas y prohibición explícita de tocar el índice de
git —la corrección del fallo de esta mañana, ya aplicada como rutina—:

- **T19**: sección del servidor MCP en los dos README. Lo primero que dice es que **el MCP
  no se conecta a SQL Server**, porque es la confusión típica: quien no lo entienda buscará
  dónde poner la cadena de conexión en el cliente.
- **`store_info` + `describe_object`**. Cierran el bucle del agente: hasta ahora resolvía un
  id y solo podía preguntar por su impacto, no **leer** el objeto ni saber si el grafo era
  de hace una hora o de hace tres meses.

Seis herramientas: `store_info`, `resolve_object`, `describe_object`, `impact`,
`column_provenance`, `column_impact`.

### Lo que no estaba previsto

- El gate del registro llamaba a `store_info` contra un fixture sin tabla `meta`, y salía
  una `SqliteException` cruda como error de protocolo en vez de `isError:true`. Envuelta en
  `McpToolException`: un store con esquema incompleto falla ahora con mensaje claro en vez
  de escupir SQL. Endurecimiento genuino, no parche para pasar el test.
- **Los gates del registro volvieron a cubrir solos** las herramientas nuevas: 4 pruebas de
  las 15 aparecieron sin que nadie las escribiera. Segunda vez que pasa hoy.

### Sobre la verificación

El informe de un agente es una afirmación; un comando es un hecho. Cuatro capas: el agente
se verifica porque el encargo se lo exige con `verificacion_ejecutada` pidiendo el resultado
**real**; se repiten los comandos; se prueba contra el store real, que es donde el corpus
sintético no llega; y los gates cubren lo que nadie escribió. El `tablas_leidas: []` de
`Website.ChangePassword` se comprobó contra las aristas antes de darlo por bueno: el objeto
solo escribe. #leccion

---

## 2026-08-19 (T17) — Herramientas de columna del MCP

**Estado**: `main` en `a29405b`, árbol limpio. **282/282** y **43/43**.

`column_provenance` y `column_impact`, nacidas ya dentro de `IMcpTool`. Son dos preguntas
distintas y a propósito no se mezclan: de dónde sale el **valor** de una columna
(`DERIVES_FROM` hacia las fuentes, ordenado de más profundo a más cercano = orden de
remediación), frente a qué se rompe al cambiarla (objetos que la referencian con su
confianza, más columnas derivadas). La confianza por objeto toma el **mejor caso**: una
referencia directa basta por muchas aristas débiles que tenga el mismo objeto.

### Lo que no estaba previsto

- **`nodes.db` viene NULL en los nodos `Column`.** El descargo de SQL dinámico sin resolver
  contaba 0 y **no se emitía nunca**: un "no hay riesgo" silencioso, exactamente el fallo
  que la regla del cero culpable existe para cazar. La base se deduce ahora del prefijo del
  id. Hay test que lo fija. #leccion
- **`out/graph_full.db` del repo está obsoleto**: esquema anterior, sin `resolution` ni
  `unresolved_dynamic_sql_steps`. La primera inspección salió con 409 aristas y `resolution`
  a NULL, y era el instrumento, no el dato. Hay que regenerarlo.
- **Los ids de tabla van en minúsculas** (`dbo.origen`); el nombre de columna conserva su
  caja. Seis tests rojos hasta darse cuenta.
- **Los gates del registro se aplicaron solos** a las dos herramientas nuevas: 14 pruebas
  más en vez de 10. Era el motivo de escribirlos recorriendo `McpToolRegistry.Default`.

### Verificado contra el store real

`LineProfit` sale de `PickedQuantity`, `UnitPrice` y `LastCostPrice` — la fórmula del
beneficio. Respuestas de 745 y 353 bytes, dentro del presupuesto de 2 KB.

### Siguiente

`describe_object` antes que T18: hoy el agente resuelve un id y **no puede leer el objeto**.
Es el peldaño que falta y es barato. Luego T18 (`diff_impact`), T19 y T20.

**Aparcado**: `agent-context-kit` (repo aparte). La investigación del estado del arte tumbó
su tesis — `AGENTS.md` ya es estándar de la Linux Foundation y `ctxlint`/`agents-lint` ya
validan contexto contra el código. Queda como está, sin trabajo pendiente aquí.

---

## 2026-08-19 (cierre) — Fase 0 completa: 0.8, 0.9 y documentación multi-modelo

**Estado**: `main` en `f478048`, árbol limpio. **268/268** y **43/43**.

- `IGraphSink` (`2299398`): los cuatro bloques de exportación de `Program.cs` pasan a un
  bucle sobre `GraphSinks.Default`. Verificado end-to-end contra `input.json`: los cuatro
  ficheros salen y las líneas de resumen son byte a byte las de antes.
- **Paso 0.9 (`610eeea`), la validación de la arquitectura**: `ParserGeneral` usa los
  mismos sinks y el grafo unificado SQL + .NET llega por primera vez a SQLite
  (1684 nodos, 4771 aristas con la fixture `efapp`).
- **Dos defectos que solo aparecen al cruzar la pila.** `resolve_object` devolvió 0
  coincidencias para un nodo .NET. No era la arquitectura: `StoreSchema.AddressableLabels`
  e `ImpactEdgeTypes` eran solo del lado SQL. Añadidos `AppProject`/`AppClass`/`AppMethod`/
  `EntryPoint`/`ExternalTarget` y las aristas puente `EXECUTES_SQL`/`MAPS_TO`. Sin la regla
  del cero culpable, ese `total:0` pasaba por respuesta legítima.
- **Honestidad sobre el alcance**: la fixture `efapp` tiene **0 aristas `EXECUTES_SQL`**, así
  que queda demostrada la direccionabilidad de los nodos de aplicación, **no** el impacto
  cruzado app→BD. Hace falta un corpus con SQL literal para eso.
- **Documentación repartida** (`f478048`): `CONTEXTO-SESION.md` se trocea en ficheros
  monotema con frontmatter YAML, más `AGENTS.md` neutral y puntos de entrada para Copilot y
  Cursor. Hecho por un subagente con brief cerrado y restricción de rutas.

**Fallo de proceso a no repetir**: el subagente dejó un `git rm` en el índice y mi
`git commit` posterior lo arrastró al commit de código, porque `commit` incluye todo lo
staged, no solo lo que yo había añadido. Trabajando en paralelo sobre el mismo árbol hay
que usar `git commit -- <rutas>` o revisar `git status` antes de cada commit.

**Siguiente**: fase 1. T17 (`column_impact`/`column_provenance`), luego `store_info` +
`describe_object`.

---

## 2026-08-19 — Fase 0 de arquitectura: pasos 0.1 a 0.8

**Estado al terminar**: `main`, árbol limpio, 7 commits nuevos sobre `a831fc1`.
Suites **268/268** (era 256, +12 gates nuevos) y **43/43**. Build sin errores.

### Qué se hizo

Se partió el proyecto-dios. Medido antes de tocar nada: de las 13.762 líneas de
`src/TSqlParser`, solo 6.124 eran T-SQL/ScriptDom; **5.051 eran agnósticas del lenguaje**
y 2.075 acceso a SQL Server vivo.

| Commit | Paso | Qué |
|---|---|---|
| `3d4705d` | 0.1+0.2 | Nace `Parser.Graph`; cruzan Sqlite/Graphify/GraphMl/Utf8Io |
| `70b7285` | 0.4 | Cruzan Audit×2, Risk, ChangeMap×2 |
| `acaa693` | 0.3 | Cruza `NodeStoreExporter` (1198 líneas, iba solo) |
| `5fc875f` | 0.5 | Cruza `AgentBench` |
| `1659adb` | 0.7 | Nace `Parser.Mcp` con frontera de compilación |
| `e7f783b` | 0.6 | `StoreSchema` en Contracts + 7 gates de vocabulario |
| `e9c0a28` | 0.8 | `IMcpTool` + registro + inyección por constructor, 5 gates |

El efecto que importa: `Parser.Graph` y `Parser.Mcp` **no referencian `TSqlParser`**, así
que el acoplamiento que había ya no es posible por construcción, no por disciplina.

### Lo que no estaba previsto

- **El orden del plan estaba mal.** `NodeStoreExporter` dependía de `AuditExporter` y
  `ChangeMapExporter`, así que no podía cruzar el primero. Se reordenó sobre la marcha.
- **Dos ficheros mal clasificados.** `BlindRefs` usa `GraphExporter` e `InputAnalyzer`;
  `ReportGenerator` usa `ObjectResult` y `SqlText`; `Models.cs` contiene `WalkContext`.
  Ninguno es agnóstico: se quedan en `TSqlParser`. La categorización inicial por "no
  menciona ScriptDom" era demasiado gruesa.
- **La frontera destapó visibilidad.** `AuditExporter` y `AuditVerifier` eran `internal` y
  funcionaban solo por vivir en el mismo proyecto. Cruzar el ensamblado obligó a
  declararlos públicos, que es exactamente lo que se buscaba: lo que cruza, se declara.
- **El gate se verificó por mutación.** Renombrar `WRITES_COLUMN` a `WRITES_COLUMNS` pone
  `StoreSchemaGateTests` en rojo nombrando al culpable. Un gate que no se ha visto fallar
  no cuenta.

### Decisiones tomadas

- MCP en biblioteca propia, no en ejecutable aparte: mantiene un único binario instalable.
- SQLite confirmado como store, con dos límites anotados en `docs/plan-arquitectura.md` §5.
- Inyección por constructor con composition root a mano; **sin contenedor de DI** en un CLI.
- El plan pasa de `notes/` (ignorado por git) a `docs/plan-arquitectura.md`.

### Siguiente paso concreto

`IGraphSink`: los cuatro bloques de exportación de `Program.cs` (~líneas 440-497) repiten
el cálculo del nombre de la BD y la derivación de extensión. Después, el paso **0.9**
(`ParserGeneral` escribiendo SQLite del grafo unificado), que es la prueba de que esta
arquitectura sirve para algo: si sale difícil, hay que revisarla antes de seguir a fase 1.

### Pendiente sin tocar

Actions en GitHub tras el primer push con CI real; `test/pr-impact-demo`; blog con 35
sustituciones aplicadas y sin commitear.
