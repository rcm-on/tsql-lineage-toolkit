# TSql Lineage Toolkit

Analizador de lineage para T-SQL (procedimientos, funciones, triggers) basado en
[ScriptDom](https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom), más un
dashboard web estático para explorar los resultados.

## ¿Para qué sirve?

Antes de tocar una tabla o un procedimiento en una base de datos SQL Server
grande, la pregunta es siempre la misma: **¿qué se rompe si cambio esto?**
Responderla a mano significa leer cientos de procedimientos a ojo.

Este toolkit recorre el AST real (no regex) de cada procedimiento/función/
trigger/vista y construye el grafo de lineage completo: qué objetos llaman a
cuáles (`CALLS`), qué tablas/columnas lee y escribe cada uno
(`READS_FROM`/`WRITES_TO`, `READS_COLUMN`/`WRITES_COLUMN`), y las relaciones de
clave foránea entre tablas. Con eso puedes:

- **Impact analysis**: "si cambio `dbo.Customers.Email`, ¿qué procedimientos
  lo leen o lo escriben, directa o indirectamente?"
- **Preparar una migración**: detectar dependencias ocultas (SQL dinámico,
  `EXEC`, vistas en cascada) antes de mover o refactorizar objetos.
- **Onboarding / auditoría**: explorar visualmente el flujo de control y de
  datos de una base de datos desconocida sin leer cada SP.

El grafo resultante (`graph_full.json`) se explora en el
[dashboard](dashboard/) (vanilla JS, sin instalación) o se exporta a
Neo4j/GraphML/D3 para análisis más avanzado.

![Dashboard: resumen general](docs/dashboard-overview.png)

## Estructura del repositorio

```
.
├── src/TSqlParser/          # Analizador .NET (CLI) y librería
│   ├── Program.cs            # Punto de entrada CLI
│   ├── SqlAnalyzer.cs        # Orquesta el análisis de cada objeto
│   ├── AstWalker.cs          # Recorrido del AST de ScriptDom
│   ├── GraphExporter.cs      # Exporta el grafo de lineage (nodos/relaciones)
│   ├── GraphMlExporter.cs    # Export a GraphML (Gephi/yEd/Cytoscape)
│   ├── GraphifyExporter.cs   # Export plano {meta,stats,nodes,edges} (D3/vis-network)
│   ├── ReportGenerator.cs    # Informe en texto/Markdown
│   ├── TableAnalyzer.cs      # Análisis de tablas
│   ├── TableSchemaExtractor.cs / DbValidator.cs
│   ├── Models.cs / SqlText.cs
│   └── queries/lineage_queries.cypher  # Consultas Cypher de ejemplo (Neo4j)
│
├── tests/TSqlParser.Tests/   # Suite de tests (xUnit)
│   ├── LineageTests.cs
│   ├── ChatGpt/              # Tests de cobertura: capacidades, fuzzing, stress, FRK
│   └── lineage_specs/        # Specs JSON de derivaciones esperadas
│
├── dashboard/                # App web estática (sin build) para explorar el grafo
│   ├── index.html
│   ├── src/                  # Componentes vanilla JS (namespace global `SD`)
│   ├── vendor/                # Dependencias vendorizadas (mermaid.js)
│   └── e2e/                   # Smoke test (Playwright) contra samples/from-sql-demo
│
├── samples/                  # Ejemplos pequeños de entrada/salida
│   ├── sample_input.json
│   ├── sample_graph.json
│   └── sample_workflows.json
│
└── TSqlLineageToolkit.sln
```

## Analizador (.NET)

Requiere .NET SDK 10.

```bash
dotnet build
dotnet test
```

### Generar el grafo de lineage

```bash
cd src/TSqlParser
dotnet run -- input.json graph_full.json workflows_full.json --columns
```

- `input.json`: array de `{ "name": "Database::Schema.Object", "sql": "CREATE PROCEDURE ..." }`.
- `--columns`: añade nodos `:Column` (HAS_COLUMN / READS_COLUMN / WRITES_COLUMN).
- `--graphify`: además exporta `<graph>.graphify.json` (D3 / vis-network / Graphify).
- `--graphml`: además exporta `<graph>.graphml` (Gephi / yEd / Cytoscape / NetworkX).
- `--nodestore`: además exporta `<graph>.nodes/`, una versión del mismo grafo
  partida en ficheros pequeños y navegables (pensada para que un agente la lea
  sin cargar `graph_full.json` entero):

  ```
  graph_full.nodes/
    index.json        # punto de entrada: meta, schema (tipos cerrados de nodo/arista),
                       # stats e instrucciones de navegación ("howto")
    model.json         # "nodos iniciales": todos los SqlObject y Table, con
                       # CALLS/AFFECTS/FK_TO y WRITES_TO/READS_FROM agregados a
                       # nivel de objeto - el mapa general desde el que decidir
                       # qué fichero abrir a continuación
    manifest.json      # por objeto: content_hash + fichero + nodos compartidos
                       # a los que contribuye (base para una futura
                       # regeneración incremental)
    objects/<obj>/object.json    # un SqlObject con sus parámetros/variables/
                       # pasos propios y sus aristas salientes (cada una con
                       # `path` al fichero vecino)
    shared/{tables,columns,actions,rules}/<id>.json  # nodos compartidos entre
                       # objetos (Table/Column/Action/Rule), con `refs`
                       # (aristas entrantes partidas por objeto contribuyente)
                       # y `edges_in`/`edges_out` ya resueltos
  ```

  Un agente típico: lee `index.json` + `model.json` (pequeños), localiza el
  objeto o tabla de interés, abre su `objects/.../object.json` o
  `shared/.../*.json`, y sigue los `path` de sus aristas solo donde necesite
  profundizar - sin tocar el resto de ficheros.

  **Ejemplo medido** (WideWorldImporters, 47 objetos, 1384 nodos, 3365
  relaciones) para responder "¿qué escribe en `Warehouse.StockItems`,
  directa e indirectamente?":

  | | ficheros leídos | bytes | tiempo |
  | --- | --- | --- | --- |
  | `graph_full.json` completo + filtrar relaciones a mano | 1 | 1.51 MB | 194 ms |
  | `index.json` + `model.json` + `shared/tables/...stockitems.json` | 3 | 93 KB | 30 ms |

  La segunda vía lee **16x menos datos** y entrega la respuesta ya
  estructurada por objeto contribuyente (`refs`), incluyendo cadenas de
  impacto indirecto con `via`/`hops` precalculados - sin reconstruirlas a
  mano a partir de las 3365 relaciones planas.

  **`manifest.json` y actualizaciones incrementales.** Por cada `SqlObject`
  guarda `{ content_hash, object_file, shared_touched }`:
  - `content_hash`: hash SHA-1 del `object.json` de ese objeto - permite
    detectar si cambió desde la última generación sin reanalizar el SQL.
  - `shared_touched`: lista de los nodos compartidos (tablas, columnas,
    acciones, reglas) a los que ese objeto contribuye en `shared/**`.

  **`update-nodestore`: actualización incremental.** Una vez generado el
  store con `--nodestore`, puedes refrescarlo sin regenerarlo entero:

  ```bash
  dotnet run -- update-nodestore ../../input.json ../../graph_full.nodes --columns
  ```

  Reanaliza todo el `input.json` en memoria (barato), pero solo **escribe**
  los ficheros `objects/**`/`shared/**` cuyo contenido cambió respecto al
  `manifest.json` existente: compara `content_hash` por objeto y, para
  `shared/**`, el contenido serializado contra lo que ya hay en disco. Los
  objetos eliminados del input y los nodos compartidos que se quedan sin
  `refs` se borran (incluyendo directorios de categoría vacíos). `model.json`,
  `manifest.json` e `index.json` se reescriben siempre (son pequeños). Si
  `<store_dir>` no existe todavía, se comporta como un `--nodestore` completo.

  Probado a escala WideWorldImporters (47 objetos, 743 nodos compartidos):
  con el input sin cambios, `update-nodestore` no reescribe nada (`Updated: 0
  objects (47 unchanged, 0 removed), shared: 0 (743 unchanged, 0 removed)`); al
  modificar el SQL de un solo procedimiento (un `UPDATE` adicional a
  `Warehouse.StockItems`), solo se reescribe ese objeto y los 3 ficheros
  `shared/**` que tocan (`Updated: 1 objects (46 unchanged, 0 removed), shared:
  3 (740 unchanged, 0 removed)`).

  **Pruebas realizadas.** `dotnet build` (0 errores) y `dotnet test` (42/42)
  pasan con el exporter activo. Ejecutado de extremo a extremo contra
  **WideWorldImporters** (base de datos real, 47 objetos): `index.json`
  reporta `orphan_edges: 0` y `unknown_labels`/`unknown_edge_types` vacíos, es
  decir, el vocabulario cerrado cubre el 100% de los nodos/aristas de una base
  de datos real. Probado también con un procedimiento sintético muy anidado
  (IF/ELSE IF/ELSE de 4 niveles, WHILE+cursor, TRY/CATCH, ~100 nodos/221
  aristas) sin errores de parseo. Y verificado que los procedimientos reales
  con más condiciones (p. ej. `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures`,
  cyclomatic_complexity=21) guardan en cada `Step` su `condition_path`
  (la condición `IF`/`WHILE` exacta bajo la que se ejecuta) y `condition_keys`,
  visibles en un único `object.json`.

## Ejecutar contra una base de datos real

Pipeline completo: extraer el SQL de los objetos -> analizar -> (opcional)
enriquecer con esquema de tablas y validar -> cargar en el dashboard.

### 1. Configuración

- SQL Server accesible (instancia local `.\SQLEXPRESS`, remota, o vía
  contenedor).
- Autenticación: por defecto se usa autenticación de Windows (Integrated
  Security). El `--server` admite el formato habitual de
  `Microsoft.Data.SqlClient`, p. ej. `.\SQLEXPRESS`, `localhost,1433`,
  `tcp:miservidor.database.windows.net`, etc.

### 2a. Alternativa sin base de datos: a partir de ficheros .sql

Si no tienes (o no quieres usar) una conexión a base de datos, puedes generar
`input.json` directamente desde ficheros `.sql` locales (uno por objeto:
`CREATE [OR ALTER] PROC/FUNCTION/TRIGGER/VIEW/TABLE`):

```bash
cd src/TSqlParser
dotnet run -- from-sql TestDb ../../input.json ruta/a/*.sql
# o un directorio completo (recursivo):
dotnet run -- from-sql TestDb ../../input.json ruta/a/carpeta
```

El "schema.nombre" se detecta de la propia instrucción `CREATE` (por defecto
`dbo` si no está cualificado). Continúa por el paso 3.

### 2b. Extraer las definiciones a `input.json` (con el propio .NET)

```bash
cd src/TSqlParser

# Toda la base de datos
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS

# Solo uno o varios objetos concretos
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS \
  --object Sales.usp_InsertSpecialDealsTemp --object dbo.usp_AddInvoice

# Por patrón (T-SQL LIKE sobre "schema.objeto")
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --like "Sales.%"

# Incluyendo además el CREATE TABLE de todas las tablas (input.json autocontenido)
dotnet run -- extract WideWorldImporters ../../input.json --server .\SQLEXPRESS --tables
```

Conecta a la base de datos indicada (puedes apuntar a cualquiera con
`--server`/nombre de base de datos) y vuelca `sys.sql_modules`
(procedimientos, funciones, triggers, vistas) al formato
`[{ "name": "Database::schema.objeto", "sql": "CREATE ..." }]` que espera el
paso 3. `--object` / `--like` permiten analizar solo un subconjunto de SQLs en
lugar de toda la base de datos; `--tables` añade además el esquema de todas
las tablas (`sys.tables`) en una sola pasada, sin necesitar el paso 4.

> Alternativa: `scripts/extract_objects.py` hace lo mismo en Python (vía
> `pyodbc`), por si prefieres ese camino o necesitas autenticación SQL con
> usuario/password (`--user`/`--password`).

### 3. Generar el grafo de lineage

```bash
cd src/TSqlParser
dotnet run -- ../../input.json ../../graph_full.json ../../workflows_full.json --columns --graphify --graphml --nodestore
```

- `input.json`: array de `{ "name": "Database::Schema.Object", "sql": "CREATE PROCEDURE ..." }`.
- `--columns`: añade nodos `:Column` (HAS_COLUMN / READS_COLUMN / WRITES_COLUMN).
- `--graphify`: además exporta `<graph>.graphify.json` (D3 / vis-network / Graphify).
- `--graphml`: además exporta `<graph>.graphml` (Gephi / yEd / Cytoscape / NetworkX).
- `--nodestore`: además exporta `<graph>.nodes/`, una versión del mismo grafo
  partida en ficheros pequeños y navegables (pensada para que un agente la lea
  sin cargar `graph_full.json` entero):

  ```
  graph_full.nodes/
    index.json        # punto de entrada: meta, schema (tipos cerrados de nodo/arista),
                       # stats e instrucciones de navegación ("howto")
    model.json         # "nodos iniciales": todos los SqlObject y Table, con
                       # CALLS/AFFECTS/FK_TO y WRITES_TO/READS_FROM agregados a
                       # nivel de objeto - el mapa general desde el que decidir
                       # qué fichero abrir a continuación
    manifest.json      # por objeto: content_hash + fichero + nodos compartidos
                       # a los que contribuye (base para una futura
                       # regeneración incremental)
    objects/<obj>/object.json    # un SqlObject con sus parámetros/variables/
                       # pasos propios y sus aristas salientes (cada una con
                       # `path` al fichero vecino)
    shared/{tables,columns,actions,rules}/<id>.json  # nodos compartidos entre
                       # objetos (Table/Column/Action/Rule), con `refs`
                       # (aristas entrantes partidas por objeto contribuyente)
                       # y `edges_in`/`edges_out` ya resueltos
  ```

  Un agente típico: lee `index.json` + `model.json` (pequeños), localiza el
  objeto o tabla de interés, abre su `objects/.../object.json` o
  `shared/.../*.json`, y sigue los `path` de sus aristas solo donde necesite
  profundizar - sin tocar el resto de ficheros.

Los ficheros de salida (`graph_full.json`, `workflows_full.json`, `*.graphml`,
`*.graphify.json`) no se versionan (ver `.gitignore`); guárdalos donde te
convenga, por ejemplo en una carpeta `output/` local.

### 4. (Opcional) Enriquecer con el esquema de tablas

```bash
dotnet run -- extract-tables ../../graph_full.json ../../input.json --server .\SQLEXPRESS
```

Para cada nodo `:Table` del grafo, obtiene su `CREATE TABLE` (columnas, tipos,
PK, FK) de la base de datos en vivo y lo añade a `input.json`. Vuelve a
ejecutar el paso 3 para regenerar el grafo con ese esquema.

### 5. (Opcional) Validar el grafo contra la base de datos

```bash
dotnet run -- validate ../../graph_full.json --server .\SQLEXPRESS
```

Compara las relaciones `FK_TO` / `CALLS` del grafo con `sys.foreign_keys` y
`sys.sql_expression_dependencies` (solo lectura, no modifica el grafo).

> Puedes usar `samples/sample_input.json` para probar el flujo de los pasos 3-5
> sin necesidad de una base de datos.

## Dashboard

App **autónoma** (sin build, sin dependencias, offline). Abre `dashboard/index.html`
en el navegador y sube el `graph_full.json` generado en el paso 3.

Para probar el dashboard sin generar nada, usa `samples/from-sql-demo/graph.json`
— es la salida de ejecutar el flujo `from-sql` (paso 2a) sobre los `.sql` de
ejemplo en `samples/from-sql-demo/sql/`. Hay un smoke test automatizado
(Playwright) en [dashboard/e2e/](dashboard/e2e/).

Ver [dashboard/README.md](dashboard/README.md) para el detalle de cada componente.
