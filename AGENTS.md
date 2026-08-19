# tsql-lineage-toolkit

Motor determinista de lineage e impacto para T-SQL: lee procedimientos (SQL Server vivo o
ficheros `.sql`), construye un grafo de qué lee qué / qué escribe dónde / qué se rompe si
lo cambias, hasta nivel de columna y a través de SQL dinámico. Lo entrega como artefactos
portables (JSON, nodestore, SQLite, dashboard) más un servidor MCP para que un agente lo
consulte en conversación. El objetivo real es soporte a la decisión para un LLM, no solo
extraer lineage: un resultado vacío nunca es "no hay impacto".

## Estructura

```
Parser.Contracts   modelo + vocabulario + StoreSchema. Cero dependencias.
Parser.Graph       capa agnóstica del lenguaje: exportadores, riesgo, change-map. Solo Contracts + Sqlite.
Parser.Mcp         servidor MCP. Solo Contracts + Sqlite. NO puede ver el parser.
TSqlParser         extractor T-SQL (ScriptDom) + acceso a SQL Server vivo + CLI.
NetParser          extractor de C# (Roslyn).
ParserGeneral      compone los dos extractores en un grafo unificado.
```

Regla: los extractores no conocen los sinks. Si `Parser.Graph`/`Parser.Mcp` necesitan
referenciar `TSqlParser`, el cambio está mal planteado.

## Verificación

```bash
dotnet build ParserGeneral.sln -c Release
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=LiveSql"
dotnet test tests/NetParser.Tests/NetParser.Tests.csproj -c Release
```

Cifras de referencia: 268/268, 43/43, 90 referencias ciegas (98,7675 % de recall laxo en
el corpus DNN). Checklist completo en `docs/guia-de-verificacion.md`.

## Trampas del entorno

- Smart App Control bloquea el DLL de Debug recién compilado (`FileLoadException
  0x800711C7`). No es un fallo del código: invoca el DLL de Release.
- No hay SQL Server local; la vía viva es el contenedor
  (`scripts/ci/restore-sample-databases.sh`).
- `notes/` está ignorado por git; lo que deba sobrevivir va en `docs/`.
- La rama por defecto es `main`, no `master`.

## Más

Índice completo de documentación, por tema y tamaño: `docs/INDICE-AGENTE.md`.
