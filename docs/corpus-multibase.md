# Ejecución multi-corpus — 2026-07-26 (primera pasada), actualizada 2026-08-01

Cuatro corpus, para responder dos preguntas que WideWorldImporters sola no puede
responder: **¿aguanta código real y feo?** y **¿el lineage sigue siendo correcto
fuera de una base de demostración?**

Ninguna cifra de aquí está estimada. La sección "Resultados" de más abajo es
histórica (motor con solo los arreglos #1-#3 integrados, commit `3a85c9b`); se
conserva porque documenta cómo se encontró y verificó cada bug. La tabla
siguiente es la **vigente**: con los 12 arreglos ya integrados (commit `c9ccd56`,
ver [`docs/ejecucion-canonica.md`](ejecucion-canonica.md)).

## Cifras actuales (12 arreglos, 2026-08-01)

| Corpus | Entrada | Objetos | Nodos | Aristas | Errores de parseo | Segundos* |
|---|---|---|---|---|---|---|
| WideWorldImporters | 0,35 MB · base viva | 47 → 64 | **1.593** | **4.382** | **0** | 3,40 |
| AdventureWorks2019 | 0,13 MB · base viva | **52** | **1.166** | **3.050** | **0** | 2,87 |
| Ola Hallengren | 0,52 MB · `from-sql` | 4 + 3 tablas | **2.339** | **7.013** | **0** | 3,03 |
| First Responder Kit | 2,29 MB · `from-sql` | 11 + 1 tabla | **6.451** | **19.596** | **0** | 7,15 |

*Tiempos medidos con el binario `Release`, un corpus cada vez, invocado vía
`dotnet <dll>` (arranque del proceso incluido) — metodología distinta a la de la
primera pasada (más abajo), así que no son comparables punto a punto con
aquellos; sí lo son entre sí, medidos todos en esta misma sesión.

**Escalado sub-lineal, reconfirmado:** de 0,35 MB a 2,29 MB (6,5×) el tiempo sube
de 3,40 s a 7,15 s (2,1×). Con AdventureWorks2019 y FRK como los dos puntos más
separados, el ajuste lineal da un suelo de arranque de ~2,6 s (incluye el
proceso `dotnet`) y un coste marginal real de **~1,9 s por MB** de T-SQL.

Movimiento respecto a la primera pasada (con solo #1-#3), y por qué **cada
corpus se movió otra vez** al integrar los arreglos #4 y #6-#12:

| Corpus | Tras #1-#3 (2026-07-26) | Tras los 12 (2026-08-01) | Causa principal del segundo movimiento |
|---|---|---|---|
| WideWorldImporters | 1.529 / 4.151 | **1.593 / 4.382** | +19 nodos `BusinessRule` (#10/#11) + `Step` recuperados en CTE/`UNION` (#11) |
| AdventureWorks2019 | 1.128 / 2.862 | **1.166 / 3.050** | mismas causas — vistas y CTEs con `WHERE` propio |
| Ola Hallengren | 2.262 / 6.726 | **2.339 / 7.013** | ídem, más lecturas de `UNION` recuperadas en `IndexOptimize` (#7) |
| First Responder Kit | 5.679 / 16.858 | **6.451 / 19.596** | el mayor salto: TVFs (`sys.dm_*`, `STRING_SPLIT`) antes invisibles ahora cuentan como fuente de lectura (#8), además de `BusinessRule` y `UNION` |

Que **ningún** corpus se quedara quieto ante los arreglos #4/#6-#12 no es una
sorpresa ni una regresión: a diferencia de #1-#3 (específicos de un patrón que
solo aparecía en Ola/AdventureWorks), estos nueve son arreglos **generales** del
motor (TVFs, `UNION`, reglas de negocio, clasificación de `RAISERROR`,
auto-recursión) que tocan construcciones presentes en los cuatro corpus. El
control de no-regresión sigue siendo válido en su forma original: dentro de cada
tanda de arreglos, los corpus no tocados por esa tanda no se movieron (ver la
tabla histórica de más abajo para la comprobación fina de #1-#3).

## Validación contra el catálogo (solo las bases vivas)

| | WideWorldImporters | AdventureWorks2019 |
|---|---|---|
| Claves ajenas vs `sys.foreign_keys` | **98 / 98** | **90 / 90** |
| Cadenas `EXEC` vs catálogo | **12 / 12** | **22 / 22** |
| Ausencias / aristas fantasma | 0 / 0 | 0 / 0 |
| Cobertura de lineage de columna | 32 / 32 (100%) | **251 / 251 (100%)** |
| `unknown_edge_types` / `unknown_labels` / `orphan_edges` | 0 | 0 |

Dos bases independientes con cero ausencias y cero aristas inventadas. El 251/251
de AdventureWorks importa más que el 32/32 de WWI: son 20 vistas frente a 3, ocho
veces más superficie de resolución de columnas, sin perder una.

## Escala del parser

| | Máximo en WWI | Ola Hallengren | First Responder Kit |
|---|---|---|---|
| Objeto más complejo | `DeactivateTemporalTables…` | `dbo.DatabaseBackup` | `dbo.sp_Blitz` |
| Líneas | 706 | ~5.000 | **10.659** |
| Complejidad ciclomática | 19 | 542 | **706** (37×) |
| Pasos | 87 | 550 | **1.328** |
| Anidación máxima | 1 | 7 | 9 |

`sp_Blitz` es un **único procedimiento de 478 KB**. Se procesa sin fallo, sin
excepción y sin artefacto truncado. (Pasos de `DatabaseBackup` y `sp_Blitz`
recontados el 2026-08-01 con los 12 arreglos: suben respecto a la primera
pasada — 541 → 550 y 1.229 → 1.328 — porque los `Step` de `WHERE` dentro de
`UNION`/CTE que antes se perdían ahora se cuentan, arreglo #11.)

---

# Hallazgos

Ordenados por gravedad. En la primera pasada (2026-07-26) ninguno estaba
arreglado todavía: el encargo de aquel día era solo medir y documentar, no
tocar el motor. Desde entonces se integraron los 12 arreglos — **todos los de
esta lista están ahora `ARREGLADO`**, incluidos el #4 y el #6, que quedaron
abiertos en la primera pasada.

## 1. GRAVE — La identidad de una tabla se parte en dos nodos (ARREGLADO)

**Dónde:** corpus Ola Hallengren, las 3 tablas del framework.

En el patrón `UPDATE <alias> SET … FROM (subconsulta) AS <alias>`, donde el alias
coincide con el nombre corto de la tabla, el motor emite **dos nodos `Table`
distintos para la misma tabla**:

```
dbo.QueueDatabase   id=OlaMaintenance:table:dbo.queuedatabase   entrantes: READS_FROM×6
QueueDatabase       id=OlaMaintenance:table:queuedatabase       entrantes: WRITES_TO×3
```

Verificado: **3 pasos** (`DatabaseBackup#step421`, `IndexOptimize#step211`,
`DatabaseIntegrityCheck#step174`) leen del nodo cualificado y escriben en el
**no cualificado**, en la misma sentencia.

**Por qué es lo más grave de la lista:** la pregunta que justifica la herramienta
es *"¿quién escribe en esta tabla?"*. Preguntada sobre `dbo.QueueDatabase`
devuelve **cero escritores**, cuando hay tres. No es una arista de menos: es un
**falso negativo silencioso en el análisis de impacto**, en el sentido peligroso
(dice que no hay riesgo cuando lo hay).

**ARREGLADO** (rama `fix/table-identity`). La hipótesis de causa raíz única que
figuraba aquí abajo **era falsa: son dos causas independientes.**

1. **`AstWalker.ResolveAlias`** solo resolvía alias contra un `NamedTableReference`
   o `VariableTableReference`. El patrón real es
   `UPDATE QueueDatabase SET … FROM (SELECT TOP 1 … FROM dbo.QueueDatabase …) QueueDatabase`,
   donde el FROM es un **`QueryDerivedTable`** (subconsulta) aliasado. Al no
   reconocerlo, el destino caía al literal desnudo. Ahora, cuando el alias apunta a
   una subconsulta, se aplana su FROM interno y, si resuelve a una única tabla real,
   se usa esa como destino.
2. **El router de `InputAnalyzer` + `TableAnalyzer`**: ver #3.

Se añadió además un tercer arreglo **general** en `GraphExporter.GetOrCreateTable`:
un índice `(bd, nombre_corto) → nombre_cualificado` para que cualquier referencia
sin cualificar resuelva al nodo ya conocido en vez de crear un gemelo. Es
ambiguo-seguro: si dos tablas cualificadas comparten nombre corto, no adivina.

**Verificado sobre el caso real:**

```
antes:  dbo.QueueDatabase  READS_FROM×6      |  QueueDatabase  WRITES_TO×3
ahora:  dbo.QueueDatabase  READS_FROM×9  WRITES_TO×18        (un solo nodo)

"¿quién escribe en dbo.QueueDatabase?"  →  antes 0 objetos, ahora 3
   dbo.DatabaseBackup · dbo.IndexOptimize · dbo.DatabaseIntegrityCheck
```

**Límite conocido que se deja abierto a propósito:** el caso simétrico inverso —una
referencia sin cualificar que aparece *antes* de que la tabla cualificada se
registre— sigue creando nodo aparte. El índice solo resuelve hacia delante. No
ocurre en los escenarios reales verificados (`BuildTableSchemas` corre primero), y
arreglarlo exigiría un segundo paso de reconciliación. Documentado, no escondido.

## 2. GRAVE — Los triggers DDL a nivel de base de datos no se extraen

**Dónde:** AdventureWorks2019.

```
sys.triggers total ............ 11
  parent_class = 0 (DDL de BD) .. 1   ← ddlDatabaseTriggerLog
  parent_class = 1 (de tabla) .. 10
en sys.objects con type='TR' .. 10
```

`ObjectExtractor` consulta `sys.sql_modules JOIN sys.objects`. Un trigger DDL de
base de datos **no está en `sys.objects`** — vive solo en `sys.triggers` con
`parent_class = 0`. El `JOIN` lo descarta. **Ningún flag lo recupera**: haría falta
una consulta aparte.

**Síntoma visible:** `dbo.DatabaseLog` se reporta como tabla huérfana cuando sí
tiene quien le escriba — ese trigger. Falso huérfano.

**Nota:** este fallo era *estructuralmente indetectable* con WideWorldImporters,
que tiene 0 triggers en catálogo. Ha salido en la primera ejecución contra la
segunda base.

**ARREGLADO** (rama `fix/ddl-triggers`). Se añade una consulta a `sys.triggers` con
`parent_class = 0`, unida a `sys.sql_modules` por `object_id` sin pasar por
`sys.objects`. Los triggers de base de datos no tienen esquema, así que se archivan
bajo el pseudo-esquema **`$database`**: no es un identificador T-SQL válido sin
corchetes, de modo que no puede colisionar con un esquema real de usuario. No hizo
falta tocar `SqlAnalyzer` ni `GraphExporter`.

Los triggers **de servidor** (`ON ALL SERVER`, `parent_class = 2`) quedan
deliberadamente fuera: viven en `sys.server_triggers`, no pertenecen a ninguna base
concreta y este extractor opera por base de datos. Ambas bases de prueba tienen 0,
así que hoy no hay impacto medible. Límite documentado, no escondido.

Resultado en AdventureWorks2019: **52 objetos** (antes 51), **1.128 / 2.862** nodos
y aristas (antes 1.120 / 2.840), y **21 tablas huérfanas** en vez de 22 —
`dbo.DatabaseLog` ya no figura.

## 3. MEDIO — `CREATE TABLE` condicional no se reconoce como tabla

**Dónde:** Ola Hallengren, los 3 ficheros de tabla.

El patrón `IF NOT EXISTS(…) BEGIN CREATE TABLE … END` —idiomático en scripts de
instalación idempotentes— produce `object_type = UNKNOWN`, y el informe dice
"Tablas (CREATE TABLE): 0" habiendo 3.

```
dbo.CommandLog       UNKNOWN
dbo.Queue            UNKNOWN
dbo.QueueDatabase    UNKNOWN
```

**ARREGLADO** (rama `fix/table-identity`). La causa: `TableAnalyzer.AnalyzeTable` y
el router de `InputAnalyzer` buscaban el `CREATE TABLE` con un `.FirstOrDefault()`
sobre los `Statements` **del nivel superior del batch**. Envuelto en un
`IfStatement`, el `CREATE TABLE` no está ahí: está anidado dentro. El script caía en
`SqlAnalyzer` como objeto programable `UNKNOWN`, sin columnas ni clave primaria.

Se resuelve con un visitor recursivo que encuentra el `CREATE TABLE` a cualquier
profundidad, más un `LooksLikeTableScript` que usa el router.

**Un efecto secundario que el propio arreglo destapó:** hubo que cubrir
explícitamente las 12 variantes de `CREATE`/`ALTER`/`CREATE OR ALTER` de
PROCEDURE/FUNCTION/TRIGGER/VIEW, porque `IndexOptimize.sql` usa `ALTER PROCEDURE`
tras un guard dinámico y contiene un `CREATE TABLE #SelectedIndexes` interno — sin
esas variantes, el router lo habría clasificado como *tabla*. Se detectó porque el
`report` decía "4 tablas" en vez de 3.

> **La hipótesis de que #1 y #3 eran la misma causa raíz era falsa.** Son dos
> defectos independientes que se manifestaban sobre las mismas tablas. Conviene
> dejarlo escrito: la corazonada era razonable y era incorrecta.

Resultado: `report` pasa de "Tablas (CREATE TABLE): 0" a **3**, y los tres scripts
dejan de contarse como objetos programables.

## 4. MEDIO — Subdetección de SQL dinámico ejecutado vía variable (ARREGLADO)

**Dónde:** Ola Hallengren.

El patrón `EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand`
(ejecución cross-database de `sp_executesql` a través de una variable que lleva el
nombre completo) se detectaba de forma incompleta: en `IndexOptimize.sql` había ~19
apariciones y solo 8 se marcaban `is_dynamic_sql`; en `DatabaseIntegrityCheck.sql`,
5 frente a 3. En `DatabaseBackup.sql` y `CommandExecute.sql`, con el mismo patrón
pero menos repeticiones, el conteo ya cuadraba.

**ARREGLADO** (commits `1b72779` y `30766ce`, rama `fix/dynamic-exec-var`). La
causa real, dos capas por debajo de la hipótesis original: `sp_executesql`
ejecutado a través de una variable no se reconocía como fuente de un `INSERT`
(`ExecuteInsertSource`), así que el lado `EXEC` del `INSERT ... EXECUTE @var`
desaparecía entero del lineage en vez de subdetectarse en bucles. No era un
problema de bucles ni de ramas anidadas — esa hipótesis, igual que la de #1/#3,
era razonable y era incorrecta.

---

## 7. GRAVE — `INSERT ... SELECT` con `UNION`/`EXCEPT`/`INTERSECT` perdía todas las lecturas (ARREGLADO)

**Dónde:** general del motor; se manifiesta en `IndexOptimize.sql` (Ola) y en
varios procedimientos del FRK que arman su resultado combinando ramas.

Cuando el origen de un `INSERT ... SELECT` (o de un `UPDATE`/`MERGE` con
subconsulta derivada) era un `UNION`/`EXCEPT`/`INTERSECT`, el analizador solo
recorría una de las ramas del operador de conjunto: las tablas leídas en las
demás ramas desaparecían del lineage sin aviso — el mismo tipo de falso negativo
silencioso que el #1, pero en un patrón distinto y mucho más común en SQL real.

**ARREGLADO** (commit `21f0074`). El recorrido ahora aplana el árbol de
`BinaryQueryExpression` (`UNION`/`EXCEPT`/`INTERSECT`, con o sin `ALL`) antes de
extraer las tablas de origen, así que cada rama aporta sus lecturas.

## 8. MEDIO — Funciones con valor de tabla invisibles para el lineage (ARREGLADO)

**Dónde:** general; el FRK es donde más pesa (`sys.dm_*`, `STRING_SPLIT`,
`OPENJSON`, `OPENQUERY`, `OPENROWSET` aparecen decenas de veces en `sp_Blitz*`).

Una función con valor de tabla (TVF definida por el usuario, función de sistema
tipo `sys.dm_exec_query_stats`, o una de las formas especiales `OPENJSON` /
`STRING_SPLIT` / `OPENQUERY` / `OPENROWSET`) usada como fuente de un `FROM` no
generaba arista `READS_FROM`: el objeto parecía no leer nada aunque su cuerpo
dependiera enteramente de esas funciones.

**ARREGLADO** (commit `0cb43ba`). Se reconoce cada una de esas formas como tabla
de origen legítima y se emite su arista de lectura. Es la causa directa de que
FRK pase de 5.679/16.858 a 6.451/19.596 nodos/aristas (§ Cifras actuales): la
mayoría del salto son las lecturas de catálogo que antes se perdían.

## 9. GRAVE — `RAISERROR` de severidad ≤10 clasificado como `THROW` (ARREGLADO)

**Dónde:** general; pesa más cuanto más código T-SQL "a la antigua" tiene el
corpus (Ola Hallengren y FRK usan `RAISERROR` como mecanismo de log/progreso, no
solo de error).

El clasificador de acciones trataba **todo** `RAISERROR` como si lanzara una
excepción real (`THROW`), sin mirar la severidad. `RAISERROR('mensaje', 0, 1)`
—severidad 0, puramente informativo, el equivalente T-SQL de un `PRINT`— se
contaba como un fallo. Contra el corpus de producción esto generó **1.012 falsos
positivos**: cada línea de log de `sp_BlitzIndex`/`sp_BlitzCache` aparecía como
un camino de error.

**ARREGLADO** (commit `99b0e14`). Ahora se distingue por severidad: `≤10` se
clasifica como acción `PRINT` (informativa); `>10`, como `THROW` real. De paso
se añadieron las acciones `BREAK`/`CONTINUE`/`GOTO`/`WAITFOR`, que tampoco se
reconocían.

## 10. MEDIO — El `WHERE` no se modelaba como regla de negocio (ARREGLADO)

**Dónde:** general.

El motor extraía qué se lee y qué se escribe, pero no las **condiciones** bajo
las que ocurre: un `WHERE` no dejaba rastro propio en el grafo más allá de
`FILTERS_ON`. Preguntas del tipo "¿qué reglas de negocio gobiernan esta tabla?"
no tenían dónde aterrizar.

**ARREGLADO** (commit `f4c4e0d`). Cada `WHERE` se modela ahora como un nodo
`:BusinessRule`, con aristas `HAS_RULE` (desde el objeto) y `CONSTRAINS` (hacia
la tabla/columna filtrada), y una propiedad `filter_kind`
(`domain_filter`/`key_lookup`/`mixed`) para distinguir un filtro de negocio de
un simple `WHERE id = @id`. Resultado medido en WWI: `summary.business_rules`
pasa de 0 a **19** (ver `docs/ejecucion-canonica.md` §2.6).

## 11. MEDIO — El `WHERE` dentro de una CTE o de una rama de `UNION` no se capturaba (ARREGLADO)

**Dónde:** general; se combina con el #7 y el #10.

El arreglo #10 cubría el `WHERE` de una sentencia de nivel superior, pero no el
de un `SELECT` anidado dentro de una CTE, ni el de cada rama de un
`UNION`/`EXCEPT`/`INTERSECT` — incluida la condición de parada de una CTE
recursiva, que es precisamente el `WHERE` más importante de detectar (define
cuándo termina la recursión).

**ARREGLADO** (commit `d39c9d9`). El recorrido que busca predicados ahora entra
en el cuerpo de cada CTE y en cada rama de un operador de conjunto. Es la otra
causa (junto al #7) de que suban los `Step` en los cuatro corpus.

## 12. BAJO — Una auto-llamada (recursión directa) no emitía arista `CALLS` (ARREGLADO)

**Dónde:** general; se manifiesta en cualquier procedimiento que se llama a sí
mismo (recursión directa, sin pasar por otro objeto intermedio).

Un procedimiento que se invoca a sí mismo (`EXEC dbo.MiProc` dentro del propio
`dbo.MiProc`) no generaba la arista `CALLS` hacia sí mismo: la cadena de
llamadas mostraba el objeto como si no tuviera recursión, cuando sí la tiene.

**ARREGLADO** (commit `d5b0e29`). El emisor de `CALLS` ya no descarta el caso en
que origen y destino son el mismo objeto.

---

# Pendiente: prototipo de resolución por placeholder

Idea para atacar el 71% sin resolver de la sección siguiente: sustituir
`QUOTENAME(@param)` por un **placeholder de identificador** para que la cadena
parsee, y quedarse con todo lo demás (tabla, columnas, operación), perdiendo solo
la identidad de la base:

```sql
'… FROM ' + QUOTENAME(@DatabaseName) + '.[sys].[objects]'
        →   SELECT … FROM [«param:@DatabaseName»].[sys].[objects]
```

Funciona porque **`QUOTENAME()` es una garantía sintáctica de posición de
identificador** — su razón de existir es producir un nombre entre corchetes. Donde
no haya esa garantía (una variable que aporta una cláusula entera), no se sustituye
y se falla cerrado como hoy.

**Requisito innegociable:** la arista resultante debe marcarse como **inferida**,
reutilizando la convención `confidence` que ya usa `PlanEnricher`. Sin esa marca se
cambia un falso negativo por un falso positivo, que es peor: destruye la propiedad
de que cuando el grafo afirma algo, es cierto. Esta función sería el primer
consumidor real del *scoring de confianza* que el README lista como limitación
pendiente.

**Estado: abierto.** Empezado en `feat/dynsql-placeholder` (commit `2a410fb`),
interrumpido a medias y **sin verificar**. Control de no-regresión para cuando se
retome: en WideWorldImporters los 34 pasos dinámicos deben seguir resolviéndose
**34/34 y con aristas ciertas, no inferidas**.

## 5. BAJO — BOM UTF-8 inconsistente entre los dos caminos de entrada

> **Corrección de una afirmación anterior de este documento.** Escribí que las
> salidas llevaban BOM y la entrada no. Es falso: comprobé solo el `input.json` del
> First Responder Kit. Medido en los dos caminos:
>
> ```
> input.json escrito por `extract`   → BOM = SÍ
> input.json escrito por `from-sql`  → BOM = no
> ```
>
> La inconsistencia real estaba **entre los dos caminos de entrada**, no entre
> entrada y salida. Las salidas sí llevaban BOM todas.

`graph_full.json`, `index.json` y todo el NodeStore se escribían con BOM
(`EF BB BF`). Consecuencia real: `json.load()` de Python falla con
`Unexpected UTF-8 BOM (decode using utf-8-sig)`. Node lo tolera. Cualquier
consumidor con parser estricto tropieza.

**ARREGLADO** (rama `fix/json-hygiene`). La causa: `File.WriteAllText(…, Encoding.UTF8)`
— el `Encoding.UTF8` estático de .NET **emite BOM**. Estaba en **30 sitios** de 7
ficheros. En vez de parchearlos uno a uno se centralizó en `Utf8Io.WriteAllText`
con `new UTF8Encoding(false)`: ahora hay una sola forma de escribir JSON en el motor.
Verificado: 0 de 1.645 ficheros con BOM, y la lectura de artefactos antiguos *con*
BOM sigue funcionando.

## 6. BAJO — `lineage_coverage: 100%` sobre denominador cero (ARREGLADO)

En el FRK, `audit_report.json` reportaba `coverage_pct: 100` con
`objects_with_output_columns: 0` y `columns_total: 0`. Un 0/0 presentado como
100% es engañoso en un informe de auditoría.

**ARREGLADO** (commit `d695a6a`, junto con el BOM del #5). `coverage_pct` ahora
sale `null` cuando el denominador es cero, con un campo `measured: false`
adicional para que un consumidor automático distinga "0% medido" de "no hay
nada que medir". Reconfirmado en esta ejecución (2026-08-01): tanto Ola Hallengren
como el FRK devuelven `"coverage_pct": null, "measured": false` — ninguno de los
dos tiene columnas de salida catalogadas (ver "Lo que estos corpus NO
demuestran").

---

# No es un fallo: el límite honesto del SQL dinámico

En el FRK, de **277 pasos de SQL dinámico catalog-driven detectados, 197 (71%)
no se resuelven** a un destino literal. La causa, verificada en el fuente
(`sp_BlitzIndex.sql:231-251`):

```sql
SET @dsql = N'SELECT @RowcountOUT = COUNT(1) FROM ' + QUOTENAME(@DatabaseName) + N'.[sys].[objects] …';
EXEC sp_executesql @dsql, @params, …;
```

El nombre de base de datos es un **parámetro de entrada**. No hay forma de saber
en análisis estático a qué base apunta: depende de con qué lo llames. El motor lo
marca como no resuelto en vez de adivinar un destino falso — el comportamiento
*fail closed* que ya documenta `index.json.howto.exec_resolution` y contabiliza en
`unresolved_dynamic_sql_steps`.

**Está bien resuelto, pero hay que decirlo en la documentación pública.** Hoy el
artículo vende "resuelve el SQL dinámico" sin matiz. Lo cierto es: lo resuelve
cuando el destino es reconstruible estáticamente; cuando depende de un parámetro,
lo marca y lo cuenta. En un corpus real ese caso fue **el 71%**. Decirlo sube la
credibilidad, no la baja.

# Lo que estos corpus NO demuestran

Ola Hallengren y el First Responder Kit **leen DMVs `sys.*` y escriben en
temporales**: apenas tocan tablas de usuario (`WRITES_TO` = 2 aristas en los 11
procedimientos del FRK), no tienen columnas de salida catalogadas, y **no hay
catálogo propio contra el que validar**. Estresan el parser a lo bestia; **no
ejercitan el lineage sobre un esquema real**. Esa parte la cubren WWI y
AdventureWorks, y solo ellas.
