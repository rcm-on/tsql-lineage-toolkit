# Recall de lineage de columna (corpus DNN)

Mide cuánto del lineage de columna que **SQL Server dice que existe** captura
nuestro motor, sobre un corpus grande de T-SQL de producción real.

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
| `oracle-columns.psv` | 7786 filas `módulo\|entidad\|columna`, todo en minúsculas. El ground-truth. |

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

```bash
# 1. Materializar el esquema en una base de trabajo
sqlcmd -S localhost\SQLEXPRESS -E -C -Q "CREATE DATABASE DnnCorpus;"
sqlcmd -S localhost\SQLEXPRESS -E -C -d DnnCorpus -b -i dnn_schema.sql

# 2. Volcar el corpus (definiciones + DDL de tablas)
TSqlParser extract DnnCorpus dnn-corpus.json --server localhost\SQLEXPRESS --tables

# 3. Volcar el oráculo con las DMV (ver extract-oracle.sql)
sqlcmd -S localhost\SQLEXPRESS -E -C -d DnnCorpus -h-1 -W -i extract-oracle.sql
```

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
mal la columna". Los datos la refutan. Clasificando las 2516 pérdidas:

| Categoría de pérdida | Cuántas |
| --- | --- |
| Columna vista, colgada de otra entidad | **1983 (79 %)** — y **1896 son oráculo=VISTA / motor=TABLA** |
| Columna **no vista en absoluto** | 533 (21 %) — 326 de tablas, 190 de vistas |

Caso testigo, `dbo.GetPortals`, que es `SELECT * FROM dbo.vw_Portals`: el motor
atribuye a `vw_portals` **y además** a `portals`, `users` y `portallocalization`.
Atraviesa la vista hasta las tablas base; la DMV se para en la vista.

Son **convenciones distintas, no un defecto**, y para análisis de impacto la del
motor es la más útil: si alguien altera `Portals.PortalName`, quieres saber que
`GetPortals` se ve afectado aunque lea a través de una vista.

Por tanto: **la cobertura real del motor es el recall laxo, y el punto ciego real
son las 533 referencias que no ve.** El recall estricto sirve como detector de
cambios en la convención, no como nota.

## Línea base (2026-08-01)

```text
oráculo=7786   aristas del grafo=7816

  recall laxo     (módulo,columna)         = 93,08 %   (6797/7302)  <- cobertura real
  recall estricto (módulo,ENTIDAD,columna) = 67,69 %   (5270/7786)
  precisión                                = 67,43 %   (5270/7816)
```

Las aristas que cuentan como referencia son `READS_COLUMN`, `FILTERS_ON` y
`WRITES_COLUMN`. Las tres: el oráculo no distingue lectura de escritura, y dejar
fuera `WRITES_COLUMN` (el error de la primera versión de este gate) descarta las
columnas destino de todo `UPDATE ... SET` y hunde la medida 20 puntos. Añadir
`CONSTRAINS`/`ASSIGNED_FROM`/`DERIVES_FROM` no sube el recall ni una décima y
desploma la precisión al 42,7 %, así que se quedan fuera.

El punto ciego restante son **533 referencias** que el motor no ve: 326 de tablas,
190 de vistas y 17 de entidades no resueltas (temporales, CTE, alias).

El plan para subir estas cifras está en [notes/task-column-recall.md](../../notes/task-column-recall.md).

## Control negativo

`Measurement_IsSensitive_ControlThatMustCollapse` perturba el oráculo renombrando
cada columna y exige que el recall se desplome a ~0. Sin él, una clave de
comparación mal formada o un conjunto vacío harían pasar el gate sin medir nada.
Un gate que no puede fallar no es un gate.
