---
title: Arquitectura
description: Mapa de la solución — qué proyecto existe, qué contiene y qué puede depender de qué.
read_when: Antes de mover o crear un fichero, o de tocar el servidor MCP o el contrato del store.
related: [docs/GLOSARIO-GRAFO.md, docs/PATRONES.md, docs/plan-arquitectura.md]
stability: durable
updated: 2026-08-19
---

# Arquitectura

Mapa de la solución: qué proyecto existe, qué contiene y **qué puede depender de qué**.
Antes de mover o crear un fichero, mira la tabla de §2: dice dónde va y por qué.

El glosario del grafo (nodos, aristas, ids, granularidad de Step) vive aparte en
`docs/GLOSARIO-GRAFO.md`. Los patrones de diseño y su estado, en `docs/PATRONES.md`.

## 1. Dirección de la dependencia

```
                      Parser.Contracts
                   (modelo, vocabulario, StoreSchema)
                     cero dependencias externas
                              ▲
              ┌───────────────┴───────────────┐
              │                               │
        Parser.Graph  ◄── Parser.Mcp      NetParser
      (capa agnóstica)   (servidor MCP)  (extractor C#)
              ▲               ▲               ▲
              │               │               │
              └──────── TSqlParser ───────────┘
                  (extractor T-SQL + CLI)
                              ▲
                        ParserGeneral
                     (compone los extractores)
```

**La flecha nunca se invierte.** `Parser.Graph` y `Parser.Mcp` no referencian `TSqlParser`
ni `NetParser`, y eso no es una convención: no está la referencia en el `.csproj`, así que
el compilador lo impide. Si un cambio parece necesitar esa referencia, lo que hay que
mover es el código, no la referencia.

`Parser.Mcp` **sí** referencia a `Parser.Graph` (desde `diff_impact`, que reutiliza
`ChangeMapDiff`). No invierte nada: el MCP consume la capa agnóstica, y la capa agnóstica
sigue sin saber que el MCP existe.

## 2. Qué vive dónde

| Proyecto | Contiene | Puede depender de |
|---|---|---|
| **Parser.Contracts** | `GraphNode`/`GraphRel`/`GraphPayload`, `Vocab` (labels y aristas conocidas), `Boundary`, `IGraphExtractor`, `StoreSchema` | nada |
| **Parser.Graph** | `Export/` (Sqlite, NodeStore, Graphify, GraphMl), `Analysis/` (Risk, Audit, AuditVerifier), `ChangeMap/`, `Bench/`, `Utf8Io` | Contracts, `Microsoft.Data.Sqlite` |
| **Parser.Mcp** | `McpServer` (JSON-RPC sobre stdio), `McpTools` y los `*Queries` (consultas puras), `IMcpTool` + `Tools/` + `McpToolRegistry` | Contracts, **Graph**, `Microsoft.Data.Sqlite` |
| **TSqlParser** | `AstWalker`, `GraphExporter`, `SqlAnalyzer`, `TableAnalyzer`, `SqlText`, `SqlFileLoader`, clasificadores, `Models`, `InputAnalyzer`, `BlindRefs`, `ReportGenerator`, acceso a SQL Server vivo, `Program.cs` (CLI) | Contracts, Graph, Mcp, `ScriptDom`, `Microsoft.Data.SqlClient` |
| **NetParser** | extractor de C# con Roslyn | Contracts |
| **ParserGeneral** | fusiona los `GraphPayload` de ambos extractores | Contracts, Graph, TSqlParser, NetParser |

### Criterio para decidir dónde va un fichero nuevo

Tres preguntas, en este orden:

1. ¿Menciona un tipo de **ScriptDom** o un modelo de parseo (`WalkContext`, `ObjectResult`,
   `FlowLinkInfo`)? → `TSqlParser`.
2. ¿Abre una conexión a **SQL Server**? → `TSqlParser/Live/`.
3. ¿Solo toca `GraphPayload`, el store SQLite o JSON? → `Parser.Graph`.

Casos que engañan y ya se decidieron: `BlindRefs` usa `GraphExporter` e `InputAnalyzer`, y
`ReportGenerator` usa `ObjectResult` y `SqlText` — **no son agnósticos**, se quedan en
`TSqlParser`. `Models.cs` contiene `WalkContext`: también se queda.

## 3. El contrato entre productor y consumidor

`Parser.Contracts/StoreSchema.cs` describe el store SQLite: nombres de tabla, claves de
`meta`, convención del id de Step (`<objId>#stepN` y `RollUpStep`), valores de
`resolution` (`direct` / `star_expanded` / `via_view`), el subconjunto de aristas que
recorre el análisis de impacto y las labels direccionables por un agente.

Vive en Contracts, y no junto al exportador, porque ata a **productor** (`SqliteExporter`)
y **consumidor** (`McpTools`, `scripts/lineage-queries.sql`). Antes de existir, `McpTools`
reescribía los tipos de arista como literales: renombrar uno en `Vocab` no rompía la
compilación y dejaba al MCP devolviendo `affected:[]` en silencio — indistinguible de
"nada depende de esto". `StoreSchemaGateTests` convierte ese fallo mudo en rojo.

## 4. El servidor MCP

- Transporte JSON-RPC 2.0 delimitado por saltos de línea sobre stdin/stdout, escrito a
  mano (el porqué, frente al SDK oficial, está en `notes/checkpoints/T16.md`).
- **No se conecta a SQL Server.** Abre en solo lectura un `graph_full.db` ya generado. La
  conexión ocurre antes, en la extracción.
- Una herramienta = una clase que implementa `IMcpTool` (nombre, descripción, esquema y
  manejador juntos) registrada en `McpToolRegistry.Default`. `tools/list` y `tools/call`
  salen los dos de esa lista, así que no se puede anunciar una herramienta inalcanzable.
- `McpServer` recibe el registro por constructor. Inyección por constructor, sin
  contenedor de DI: el composition root es `Program.cs`.
- **Presupuesto de respuesta: 2 KB** (`McpTools.ResponseBudgetBytes`). Es un gate de
  diseño, no una recomendación: la razón de ser del MCP es caber en el contexto de un
  agente.
- Una respuesta vacía siempre lleva `reason`, y `hint` cuando la dirección contraria sí
  tiene resultados.

Los patrones de diseño de cada componente (Strategy/Pipeline/Visitor, y qué se descartó a
propósito) están en `docs/PATRONES.md`.
