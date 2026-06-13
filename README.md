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
│   └── vendor/                # Dependencias vendorizadas (mermaid.js)
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

### Validar contra una base de datos en vivo (opcional)

```bash
dotnet run -- validate graph_full.json --server <servidor>
```

Compara las relaciones `FK_TO` / `CALLS` del grafo con `sys.foreign_keys` y
`sys.sql_expression_dependencies` (solo lectura, no modifica el grafo).

Puedes usar `samples/sample_input.json` para probar rápidamente el flujo completo.

## Dashboard

App **autónoma** (sin build, sin dependencias, offline). Abre `dashboard/index.html`
en el navegador y sube el `workflows_full.json` generado por el analizador.

Ver [dashboard/README.md](dashboard/README.md) para el detalle de cada componente.
