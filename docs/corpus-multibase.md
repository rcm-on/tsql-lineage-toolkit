# Corrida multi-corpus — 2026-07-26

Cuatro corpus, para responder dos preguntas que WideWorldImporters sola no puede
responder: **¿aguanta código real y feo?** y **¿el lineage sigue siendo correcto
fuera de una base de demostración?**

Motor sin tocar (commit `3a85c9b`). Ninguna cifra de aquí está estimada.

## Resultados

Cifras **después** de los arreglos descritos en la sección de hallazgos (los seis se
encontraron con la primera pasada y cuatro se corrigieron; la tabla es de la
segunda pasada, con el motor ya corregido):

| Corpus | Entrada | Objetos | Nodos | Aristas | Errores de parseo | Segundos |
|---|---|---|---|---|---|---|
| WideWorldImporters | 0,35 MB · base viva | 47 → 64 | 1.529 | 4.151 | **0** | 1,69 |
| AdventureWorks2019 | 0,13 MB · base viva | **52** | 1.128 | 2.862 | **0** | 1,51 |
| Ola Hallengren | 0,52 MB · `from-sql` | 4 + 3 tablas | 2.262 | 6.726 | **0** | 1,71 |
| First Responder Kit | 2,29 MB · `from-sql` | 12 | 5.679 | 16.858 | **0** | 3,99 |

Movimiento respecto a la primera pasada, y por qué:

| Corpus | Antes | Ahora | Causa |
|---|---|---|---|
| WideWorldImporters | 1.529 / 4.151 | **1.529 / 4.151** | **sin cambios** — ningún arreglo le aplica. Es el control de no-regresión |
| AdventureWorks2019 | 1.120 / 2.840 | 1.128 / 2.862 | +1 objeto: el trigger DDL de base de datos que antes no se extraía (#2) |
| Ola Hallengren | 2.254 / 6.515 | 2.262 / 6.726 | 3 tablas antes `UNKNOWN` ahora aportan columnas y claves (#3); sube más de lo que baja al fusionar el nodo gemelo (#1) |
| First Responder Kit | 5.705 / 16.900 | **5.679 / 16.858** | **baja**: 126 → 125 tablas. El arreglo de identidad (#1) también fusionó un gemelo aquí, sin que nadie lo hubiera detectado |

Que WideWorldImporters no se mueva **ni un nodo** tras tres cambios de motor es el
dato de control: confirma que los arreglos tocan solo lo que debían.

Tiempos medidos **en serie**, un corpus cada vez, sin otros procesos compilando o
analizando: medirlos en paralelo los habría inflado. Solo el paso de construcción
del grafo (`--columns --sqlite --nodestore`), sin extracción ni capturas.

**Escalado sub-lineal:** de 0,35 MB a 2,29 MB (6,5×) el tiempo solo pasa de 2,30 s
a 4,63 s (2,0×). Hay un suelo fijo de arranque de ~2 s; el coste marginal real es
de **~1,2 s por MB** de T-SQL.

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
| Pasos | 87 | 541 | **1.229** |
| Anidación máxima | 1 | 7 | 9 |

`sp_Blitz` es un **único procedimiento de 480 KB**. Se procesa sin fallo, sin
excepción y sin artefacto truncado.

---

# Hallazgos

Ordenados por gravedad. **Ninguno arreglado**: este encargo no toca el motor, y
arreglarlos movería las cifras que se acaban de congelar.

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
que tiene 0 triggers en catálogo. Ha salido en la primera corrida contra la
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

## 4. MEDIO — Subdetección de SQL dinámico ejecutado vía variable (ABIERTO)

**Dónde:** Ola Hallengren.

El patrón `EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand`
(ejecución cross-database de `sp_executesql` a través de una variable que lleva el
nombre completo) se detecta de forma incompleta: en `IndexOptimize.sql` hay ~19
apariciones y solo 8 se marcan `is_dynamic_sql`; en `DatabaseIntegrityCheck.sql`,
5 frente a 3. En `DatabaseBackup.sql` y `CommandExecute.sql`, con el mismo patrón
pero menos repeticiones, el conteo cuadra exacto.

Apunta a que el detector se pierde en bucles o ramas anidadas con muchas
repeticiones del mismo patrón. **Hipótesis sin confirmar.**

**Estado: abierto.** Se empezó en la rama `fix/dynamic-exec-var` (commit `d613706`)
y quedó **a medias, sin pruebas y sin verificar** — cambios sueltos en
`AstWalker.cs`, sin compilar siquiera. Está commiteado y etiquetado como WIP para
poder retomarlo, **no para integrarlo**.

---

# Pendiente: prototipo de resolución por placeholder

Idea para atacar el 68% sin resolver de la sección siguiente: sustituir
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

## 6. BAJO — `lineage_coverage: 100%` sobre denominador cero

En el FRK, `audit_report.json` reporta `coverage_pct: 100` con
`objects_with_output_columns: 0` y `columns_total: 0`. Un 0/0 presentado como
100% es engañoso en un informe de auditoría.

---

# No es un fallo: el límite honesto del SQL dinámico

En el FRK, de **241 pasos de SQL dinámico detectados, 164 (68%) no se resuelven**
a un destino literal. La causa, verificada en el fuente
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
lo marca y lo cuenta. En un corpus real ese caso fue **el 68%**. Decirlo sube la
credibilidad, no la baja.

# Lo que estos corpus NO demuestran

Ola Hallengren y el First Responder Kit **leen DMVs `sys.*` y escriben en
temporales**: apenas tocan tablas de usuario (`WRITES_TO` = 2 aristas en los 12
procedimientos del FRK), no tienen columnas de salida catalogadas, y **no hay
catálogo propio contra el que validar**. Estresan el parser a lo bestia; **no
ejercitan el lineage sobre un esquema real**. Esa parte la cubren WWI y
AdventureWorks, y solo ellas.
