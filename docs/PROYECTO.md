---
title: Proyecto
description: Qué es el producto, para quién y su objetivo real de soporte a la decisión.
read_when: Antes de tocar cualquier parte del motor, para no perder de vista el objetivo real.
related: [docs/ARQUITECTURA.md, docs/plan-arquitectura.md, docs/BITACORA.md]
stability: durable
updated: 2026-08-19
---

# Proyecto

## Qué es esto en dos frases

Motor determinista de lineage e impacto para T-SQL: lee procedimientos (de un SQL Server
vivo o de ficheros `.sql`), construye un grafo consultable de qué lee qué / qué escribe
dónde / qué se rompe si lo cambias, hasta nivel de columna y a través del SQL dinámico, y
lo entrega como artefactos portables (JSON, nodestore, SQLite, dashboard) más un servidor
MCP para que un agente lo consulte en conversación.

El objetivo real no es "extraer lineage": es **soporte a la decisión para un LLM** —
impacto, remediación ordenada y visión macro. Por eso la completitud de la extracción es
la prioridad de solidez número uno, y por eso un resultado vacío nunca puede leerse como
"no hay impacto".

## Estado actual (resumen)

Detalle completo del plan en `docs/plan-arquitectura.md`, y de la última sesión en
`docs/BITACORA.md` (entrada más reciente arriba). Resumen:

- **Fase 0 (arquitectura)**: pasos 0.1 a 0.8 hechos. Queda `IGraphSink` y el paso 0.9
  (`ParserGeneral` escribiendo SQLite del grafo unificado), que es **la prueba de que la
  arquitectura sirve**: si sale difícil, hay que revisarla antes de seguir.
- **Fase 1 (producto)**: T17 herramientas de columna del MCP, T18 `diff_impact`,
  `store_info` + `describe_object`, `quickstart` + prompts + documentación del MCP,
  `IRiskRule` + `risks`.
- **Fase 2 (con red)**: partir `GraphExporter.Build` (~1167 líneas en un método), migrar
  `AstWalker` a visitors de ScriptDom, `ISqlCatalog`.

## Decisiones abiertas

- `test/pr-impact-demo`: única copia publicada de documentos que hoy solo viven en
  `notes/`. Rescatar a `docs/` o dejar como archivo.
- Blog (`quarz-blog`): 35 sustituciones aplicadas y sin commitear; falta decidir el rótulo
  `Gate / Oráculo` del diagrama Mermaid.
- NuGet: aparcado. Cuando toque, `0.1.0-preview.1`.
