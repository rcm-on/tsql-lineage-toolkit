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
dotnet build TSqlLineageToolkit.slnx -c Release
dotnet test TSqlLineageToolkit.slnx -c Release --filter "Category!=LiveSql"
```

Cifras de referencia: 439/439 (396 + 43, un solo comando desde que la solucion es la .slnx), 22 referencias ciegas (99,6987 % de recall laxo en
el corpus DNN). Checklist completo en `docs/guia-de-verificacion.md`.

El recall no es la única medida: `coverage` reconcilia módulo a módulo y `syntax-coverage`
declara los tipos de nodo sin caso en el recorrido. Ver `docs/prompt-validacion-externa.md`,
Parte F — hay defectos reales que dejan el recall EXACTAMENTE igual.

## Compilar y probar

En Windows, **usa el contenedor**:

```bash
cp .env.example .env
docker compose run --rm sdk dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --artifacts-path /repo/.artifacts-linux --filter "Category!=LiveSql"
```

Smart App Control bloquea cualquier binario recién compilado con `FileLoadException
0x800711C7`: un DLL nuevo tiene un hash que nadie ha visto, luego no tiene reputación,
luego no carga — y no la va a ganar, porque cada compilación produce un hash distinto. La
nota que decía "invoca el DLL de Release" describía una casualidad, no una regla: el
Release funcionaba solo mientras no se recompilara. Compilar dentro del contenedor lo
esquiva y además prueba sobre Linux, que es donde corre el CI.

## Trampas del entorno

- No hay SQL Server local; la vía viva es el contenedor (`docker compose up -d mssql` y
  luego `eng/restore-sample-databases.sh`).
- `notes/` está ignorado por git; lo que deba sobrevivir va en `docs/`.
- La rama por defecto es `main`, no `master`.

## Más

Índice completo de documentación, por tema y tamaño: `docs/INDICE-AGENTE.md`.
