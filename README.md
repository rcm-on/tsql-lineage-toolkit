# TSql Lineage Toolkit

Analizador de lineage para T-SQL (procedimientos, funciones, triggers) basado en
[ScriptDom](https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom), más un
dashboard web estático para explorar los resultados.

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
dotnet run -- ../../input.json ../../graph_full.json ../../workflows_full.json --columns --graphify --graphml
```

- `input.json`: array de `{ "name": "Database::Schema.Object", "sql": "CREATE PROCEDURE ..." }`.
- `--columns`: añade nodos `:Column` (HAS_COLUMN / READS_COLUMN / WRITES_COLUMN).
- `--graphify`: además exporta `<graph>.graphify.json` (D3 / vis-network / Graphify).
- `--graphml`: además exporta `<graph>.graphml` (Gephi / yEd / Cytoscape / NetworkX).

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
