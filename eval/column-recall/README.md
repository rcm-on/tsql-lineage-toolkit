# Recall de lineage de columna

Mide cuánto del lineage de columna que **SQL Server dice que existe** captura
nuestro motor, sobre corpus de T-SQL de producción real. Los corpus se declaran en
[`eval/corpora.json`](../corpora.json); hoy son dos:

| Corpus | Oráculo | Recall laxo | Recall estricto | Precisión `direct` |
|---|---:|---:|---:|---:|
| `dnn` — DNN Platform | 7.786 | **98,1 %** | 96,6 % | 99,78 % |
| `wwidw` — WideWorldImportersDW | 627 | **84,9 %** | 76,4 % | **100 %** |

Los 13 puntos de diferencia son la razón de tener el segundo. WWI-DW aporta ETL
`staging`→`dim`/`fact` y `MERGE` a volumen, que DNN no tiene, y su precisión del
100 % sobre 479 aristas dice que el hueco es **de cobertura, no de invención**: lo
que emite es correcto, simplemente no ve todo. El resto de este documento describe
el corpus `dnn`, que es el que da la señal fina.

Lo ejecuta [`ColumnRecallGateTests`](../../tests/TSqlParser.Tests/ColumnRecallGateTests.cs).
A diferencia de [`view-lineage`](../view-lineage/), este gate **no necesita SQL
Server**: corpus y oráculo están congelados aquí, así que corre en cualquier
runner.

## Por qué este corpus

AdventureWorks y WideWorldImporters, juntas, dan **881** referencias de columna.
Son demasiado pequeñas y demasiado limpias para medir nada: no tienen funciones
de ventana, casi no tienen `SELECT *` sobre vistas y sus procedimientos son
cortos. DNN aporta **7786** referencias sobre 679 procedimientos escritos a lo
largo de veinte años. La señal simplemente no existía antes.

## Ficheros

| Fichero | Qué es |
|---|---|
| `dnn-corpus.json` | 739 módulos + 128 tablas de DNN Platform, en el formato de entrada del pipeline. |
| `catalog-columns.psv` | 7786 filas `módulo\|entidad\|columna`, todo en minúsculas. La verdad de referencia, extraída del catálogo de SQL Server. |

## Procedencia y licencia

El corpus procede de [DNN Platform](https://github.com/dnnsoftware/Dnn.Platform)
(DotNetNuke), **licencia MIT**. Se construye desde su
`DNN Platform/Website/Providers/DataProviders/SqlDataProvider/DotNetNuke.Schema.SqlDataProvider`
(~851 KB) sustituyendo los dos marcadores que el instalador reemplaza en runtime:

```bash
sed -e 's/{databaseOwner}/dbo./g' -e 's/{objectQualifier}//g' \
    DotNetNuke.Schema.SqlDataProvider > dnn_schema.sql
```

## Cómo regenerar

Este corpus está declarado en [`eval/corpora.json`](../corpora.json), así que la
regeneración es un solo comando (ver [`eval/README.md`](../README.md)):

```bash
TSqlParser corpus refresh dnn            # regenera y DIFFEA, sin escribir (sale 2 si hay deriva)
TSqlParser corpus refresh dnn --write    # además sobrescribe los ficheros de aquí
```

Lo único que sigue siendo manual es materializar la base la primera vez:

```bash
sqlcmd -S localhost\SQLEXPRESS -E -C -Q "CREATE DATABASE DnnCorpus;"
sqlcmd -S localhost\SQLEXPRESS -E -C -d DnnCorpus -b -i dnn_schema.sql
```

`--write` **no toca los suelos**: son cifras medidas, hay que correr el gate y subirlas
a mano — y en un commit separado de cualquier cambio del motor.

El oráculo son las filas de `sys.dm_sql_referenced_entities` con
`referenced_minor_id > 0`, que es como SQL Server resuelve "qué columna de qué
entidad lee este módulo". No lo calculamos nosotros.

## Qué mide, y por qué tres métricas

| Métrica | Qué responde |
|---|---|
| **recall laxo** `(módulo, columna)` | **La cobertura real.** ¿Ve el motor esta columna en este módulo? |
| **recall estricto** `(módulo, ENTIDAD, columna)` | Concordancia literal con la convención del oráculo. |
| **precisión** | Qué fracción de lo que el motor emite está respaldada por el oráculo. |

La precisión no es decorativa. Un gate que solo mide recall aprueba a un motor
que invente aristas, y ese fue exactamente el agujero que tenía `DbValidator`
con las aristas `CALLS`: solo miraba las que faltaban, nunca las que sobraban.

### El recall estricto no mide calidad — cuidado con leerlo mal

La primera lectura de la diferencia entre laxo y estricto fue "el motor atribuye
mal la columna". Los datos la refutan. Clasificando las 2419 pérdidas:

| Categoría de pérdida | Cuántas |
| --- | --- |
| Columna vista, colgada de otra entidad | **1966 (81 %)** — y **1896 son oráculo=VISTA / motor=TABLA** |
| Columna **no vista en absoluto** | 453 (19 %) — 246 de tablas, 190 de vistas, 17 sin resolver |

Caso testigo, `dbo.GetPortals`, que es `SELECT * FROM dbo.vw_Portals`: el motor
atribuye a `vw_portals` **y además** a `portals`, `users` y `portallocalization`.
Atraviesa la vista hasta las tablas base; la DMV se para en la vista.

Son **convenciones distintas, no un defecto**, y para análisis de impacto la del
motor es la más útil: si alguien altera `Portals.PortalName`, quieres saber que
`GetPortals` se ve afectado aunque lea a través de una vista.

Por tanto: **la cobertura real del motor es el recall laxo, y el punto ciego real
son las 453 referencias que no ve.** El recall estricto sirve como detector de
cambios en la convención, no como nota.

## Línea base (2026-08-01)

```text
oráculo=7786   aristas del grafo=7915

  recall laxo     (módulo,columna)         = 94,17 %   <- cobertura real
  recall estricto (módulo,ENTIDAD,columna) = 68,93 %
  precisión                                = 67,81 %
```

Las aristas que cuentan como referencia son `READS_COLUMN`, `FILTERS_ON` y
`WRITES_COLUMN`. Las tres: el oráculo no distingue lectura de escritura, y dejar
fuera `WRITES_COLUMN` (el error de la primera versión de este gate) descarta las
columnas destino de todo `UPDATE ... SET` y hunde la medida 20 puntos. Añadir
`CONSTRAINS`/`ASSIGNED_FROM`/`DERIVES_FROM` no sube el recall ni una décima y
desploma la precisión al 42,7 %, así que se quedan fuera.

El punto ciego restante son **453 referencias**: 246 de tablas, 190 de vistas
y 17 de entidades no resueltas (temporales, CTE, alias).

Recorrido de la cobertura: **67,9 % → 89,1 %** (contar `WRITES_COLUMN`) **→ 93,1 %**
(expandir `SELECT alias.*`) **→ 93,5 %** (`SELECT @var = Col`) **→ 94,2 %**
(resolver columnas sin cualificar contra el esquema en JOINs).

El plan para subir estas cifras está en [notes/task-column-recall.md](../../notes/task-column-recall.md).

## Control negativo

`Measurement_IsSensitive_ControlThatMustCollapse` perturba el oráculo renombrando
cada columna y exige que el recall se desplome a ~0. Sin él, una clave de
comparación mal formada o un conjunto vacío harían pasar el gate sin medir nada.
Un gate que no puede fallar no es un gate.
