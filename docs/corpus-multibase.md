# Corrida multi-corpus — 2026-07-26

Cuatro corpus, para responder dos preguntas que WideWorldImporters sola no puede
responder: **¿aguanta código real y feo?** y **¿el lineage sigue siendo correcto
fuera de una base de demostración?**

Motor sin tocar (commit `3a85c9b`). Ninguna cifra de aquí está estimada.

## Resultados

| Corpus | Entrada | Objetos | Nodos | Aristas | Errores de parseo | Segundos |
|---|---|---|---|---|---|---|
| WideWorldImporters | 0,35 MB · base viva | 47 → 64 | 1.529 | 4.151 | **0** | 2,30 |
| AdventureWorks2019 | 0,13 MB · base viva | 51 | 1.120 | 2.840 | **0** | 2,38 |
| Ola Hallengren | 0,52 MB · `from-sql` | 7 | 2.254 | 6.515 | **0** | 2,54 |
| First Responder Kit | 2,29 MB · `from-sql` | 12 | 5.705 | 16.900 | **0** | 4,63 |

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

## 1. GRAVE — La identidad de una tabla se parte en dos nodos, y las escrituras se pierden

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

**Hipótesis a comprobar antes de arreglar el #1:** puede que #1 y #3 sean el mismo
fallo. Si la tabla no se registra en el mapa de esquemas (#3), el resolutor no
tiene con qué normalizar la referencia sin cualificar a `dbo.`, y aparece el nodo
gemelo (#1). Merece investigarse como una sola causa raíz, no como dos parches.

## 4. MEDIO — Subdetección de SQL dinámico ejecutado vía variable

**Dónde:** Ola Hallengren.

El patrón `EXECUTE @CurrentDatabase_sp_executesql @stmt = @CurrentCommand`
(ejecución cross-database de `sp_executesql` a través de una variable que lleva el
nombre completo) se detecta de forma incompleta: en `IndexOptimize.sql` hay ~19
apariciones y solo 8 se marcan `is_dynamic_sql`; en `DatabaseIntegrityCheck.sql`,
5 frente a 3. En `DatabaseBackup.sql` y `CommandExecute.sql`, con el mismo patrón
pero menos repeticiones, el conteo cuadra exacto.

Apunta a que el detector se pierde en bucles o ramas anidadas con muchas
repeticiones del mismo patrón.

## 5. BAJO — BOM UTF-8 en todas las salidas, pero no en la entrada

Verificado: `graph_full.json`, `index.json` y todo el NodeStore se escriben con BOM
(`EF BB BF`); `input.json` no. Afecta también al grafo canónico de WWI.

Consecuencia real: `json.load()` de Python falla con
`Unexpected UTF-8 BOM (decode using utf-8-sig)`. Node lo tolera. Cualquier
consumidor con parser estricto tropieza. Es fricción de consumo, no afecta al
lineage — pero la herramienta se vende como "artefacto portable y diffable".

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
