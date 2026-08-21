---
title: Patrones de diseño
description: Qué patrón va en cada sitio del motor y qué se descartó a propósito.
read_when: Antes de introducir una abstracción nueva o de tocar un sitio ya decidido.
related: [docs/ARQUITECTURA.md, docs/plan-arquitectura.md]
stability: durable
updated: 2026-08-21
---

# Patrones, y dónde NO aplicarlos

Distinción que decide cada caso: **Strategy** = elegir una de N alternativas
intercambiables. **Pipeline** = ejecutar las N en orden.

| Sitio | Patrón | Estado |
|---|---|---|
| Herramientas MCP | Strategy + registro (`IMcpTool`) | hecho |
| Exportadores | Strategy + registro (`IGraphSink`) | hecho |
| Reglas de riesgo | Strategy + registro (`IRiskRule`) | pendiente |
| Orígenes de entrada | Strategy (`IObjectSource`) | pendiente |
| Subcomandos del CLI | Command (`ISubcommand`) | pendiente |
| Acceso a SQL Server | Inversión de dependencia (`ISqlCatalog`) | pendiente |
| `GraphExporter.Build` | **Pipeline** de pasos, no Strategy | pendiente |
| `AstWalker` | **Visitor** — ScriptDom ya lo ofrece (`TSqlFragmentVisitor`) | pendiente |

Lo que se descartó a propósito, para no volver a discutirlo: repositorio genérico,
contenedor de DI y fábricas abstractas. No hay un segundo cliente de casi nada, y
abstraer sin él es coste sin retorno.

Detalle de implementación de cada fila (interfaces, ficheros, orden) en
`docs/plan-arquitectura.md` §2.
