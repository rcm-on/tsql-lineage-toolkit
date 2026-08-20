---
title: Bitácora
description: Qué cambió en cada sesión, lo más reciente arriba.
read_when: Para saber qué pasó en las últimas sesiones antes de continuar el trabajo.
related: [docs/PROYECTO.md, docs/plan-arquitectura.md, docs/CONVENCIONES.md]
stability: volatile
updated: 2026-08-19
---

# Bitácora

Qué cambió en cada sesión, lo más reciente arriba. **Se escribe al terminar la sesión,
siempre**, aunque la sesión haya sido corta o haya salido mal — una sesión que no dejó
nada escrito es una sesión que hay que reconstruir leyendo commits.

Formato de una entrada: qué se hizo, qué se aprendió que no estaba previsto, en qué estado
queda el árbol y cuál es el siguiente paso concreto.

---

## 2026-08-19 (T17) — Herramientas de columna del MCP

**Estado**: `main` en `a29405b`, árbol limpio. **282/282** y **43/43**.

`column_provenance` y `column_impact`, nacidas ya dentro de `IMcpTool`. Son dos preguntas
distintas y a propósito no se mezclan: de dónde sale el **valor** de una columna
(`DERIVES_FROM` hacia las fuentes, ordenado de más profundo a más cercano = orden de
remediación), frente a qué se rompe al cambiarla (objetos que la referencian con su
confianza, más columnas derivadas). La confianza por objeto toma el **mejor caso**: una
referencia directa basta por muchas aristas débiles que tenga el mismo objeto.

### Lo que no estaba previsto

- **`nodes.db` viene NULL en los nodos `Column`.** El descargo de SQL dinámico sin resolver
  contaba 0 y **no se emitía nunca**: un "no hay riesgo" silencioso, exactamente el fallo
  que la regla del cero culpable existe para cazar. La base se deduce ahora del prefijo del
  id. Hay test que lo fija. #leccion
- **`out/graph_full.db` del repo está obsoleto**: esquema anterior, sin `resolution` ni
  `unresolved_dynamic_sql_steps`. La primera inspección salió con 409 aristas y `resolution`
  a NULL, y era el instrumento, no el dato. Hay que regenerarlo.
- **Los ids de tabla van en minúsculas** (`dbo.origen`); el nombre de columna conserva su
  caja. Seis tests rojos hasta darse cuenta.
- **Los gates del registro se aplicaron solos** a las dos herramientas nuevas: 14 pruebas
  más en vez de 10. Era el motivo de escribirlos recorriendo `McpToolRegistry.Default`.

### Verificado contra el store real

`LineProfit` sale de `PickedQuantity`, `UnitPrice` y `LastCostPrice` — la fórmula del
beneficio. Respuestas de 745 y 353 bytes, dentro del presupuesto de 2 KB.

### Siguiente

`describe_object` antes que T18: hoy el agente resuelve un id y **no puede leer el objeto**.
Es el peldaño que falta y es barato. Luego T18 (`diff_impact`), T19 y T20.

**Aparcado**: `agent-context-kit` (repo aparte). La investigación del estado del arte tumbó
su tesis — `AGENTS.md` ya es estándar de la Linux Foundation y `ctxlint`/`agents-lint` ya
validan contexto contra el código. Queda como está, sin trabajo pendiente aquí.

---

## 2026-08-19 (cierre) — Fase 0 completa: 0.8, 0.9 y documentación multi-modelo

**Estado**: `main` en `f478048`, árbol limpio. **268/268** y **43/43**.

- `IGraphSink` (`2299398`): los cuatro bloques de exportación de `Program.cs` pasan a un
  bucle sobre `GraphSinks.Default`. Verificado end-to-end contra `input.json`: los cuatro
  ficheros salen y las líneas de resumen son byte a byte las de antes.
- **Paso 0.9 (`610eeea`), la validación de la arquitectura**: `ParserGeneral` usa los
  mismos sinks y el grafo unificado SQL + .NET llega por primera vez a SQLite
  (1684 nodos, 4771 aristas con la fixture `efapp`).
- **Dos defectos que solo aparecen al cruzar la pila.** `resolve_object` devolvió 0
  coincidencias para un nodo .NET. No era la arquitectura: `StoreSchema.AddressableLabels`
  e `ImpactEdgeTypes` eran solo del lado SQL. Añadidos `AppProject`/`AppClass`/`AppMethod`/
  `EntryPoint`/`ExternalTarget` y las aristas puente `EXECUTES_SQL`/`MAPS_TO`. Sin la regla
  del cero culpable, ese `total:0` pasaba por respuesta legítima.
- **Honestidad sobre el alcance**: la fixture `efapp` tiene **0 aristas `EXECUTES_SQL`**, así
  que queda demostrada la direccionabilidad de los nodos de aplicación, **no** el impacto
  cruzado app→BD. Hace falta un corpus con SQL literal para eso.
- **Documentación repartida** (`f478048`): `CONTEXTO-SESION.md` se trocea en ficheros
  monotema con frontmatter YAML, más `AGENTS.md` neutral y puntos de entrada para Copilot y
  Cursor. Hecho por un subagente con brief cerrado y restricción de rutas.

**Fallo de proceso a no repetir**: el subagente dejó un `git rm` en el índice y mi
`git commit` posterior lo arrastró al commit de código, porque `commit` incluye todo lo
staged, no solo lo que yo había añadido. Trabajando en paralelo sobre el mismo árbol hay
que usar `git commit -- <rutas>` o revisar `git status` antes de cada commit.

**Siguiente**: fase 1. T17 (`column_impact`/`column_provenance`), luego `store_info` +
`describe_object`.

---

## 2026-08-19 — Fase 0 de arquitectura: pasos 0.1 a 0.8

**Estado al terminar**: `main`, árbol limpio, 7 commits nuevos sobre `a831fc1`.
Suites **268/268** (era 256, +12 gates nuevos) y **43/43**. Build sin errores.

### Qué se hizo

Se partió el proyecto-dios. Medido antes de tocar nada: de las 13.762 líneas de
`src/TSqlParser`, solo 6.124 eran T-SQL/ScriptDom; **5.051 eran agnósticas del lenguaje**
y 2.075 acceso a SQL Server vivo.

| Commit | Paso | Qué |
|---|---|---|
| `3d4705d` | 0.1+0.2 | Nace `Parser.Graph`; cruzan Sqlite/Graphify/GraphMl/Utf8Io |
| `70b7285` | 0.4 | Cruzan Audit×2, Risk, ChangeMap×2 |
| `acaa693` | 0.3 | Cruza `NodeStoreExporter` (1198 líneas, iba solo) |
| `5fc875f` | 0.5 | Cruza `AgentBench` |
| `1659adb` | 0.7 | Nace `Parser.Mcp` con frontera de compilación |
| `e7f783b` | 0.6 | `StoreSchema` en Contracts + 7 gates de vocabulario |
| `e9c0a28` | 0.8 | `IMcpTool` + registro + inyección por constructor, 5 gates |

El efecto que importa: `Parser.Graph` y `Parser.Mcp` **no referencian `TSqlParser`**, así
que el acoplamiento que había ya no es posible por construcción, no por disciplina.

### Lo que no estaba previsto

- **El orden del plan estaba mal.** `NodeStoreExporter` dependía de `AuditExporter` y
  `ChangeMapExporter`, así que no podía cruzar el primero. Se reordenó sobre la marcha.
- **Dos ficheros mal clasificados.** `BlindRefs` usa `GraphExporter` e `InputAnalyzer`;
  `ReportGenerator` usa `ObjectResult` y `SqlText`; `Models.cs` contiene `WalkContext`.
  Ninguno es agnóstico: se quedan en `TSqlParser`. La categorización inicial por "no
  menciona ScriptDom" era demasiado gruesa.
- **La frontera destapó visibilidad.** `AuditExporter` y `AuditVerifier` eran `internal` y
  funcionaban solo por vivir en el mismo proyecto. Cruzar el ensamblado obligó a
  declararlos públicos, que es exactamente lo que se buscaba: lo que cruza, se declara.
- **El gate se verificó por mutación.** Renombrar `WRITES_COLUMN` a `WRITES_COLUMNS` pone
  `StoreSchemaGateTests` en rojo nombrando al culpable. Un gate que no se ha visto fallar
  no cuenta.

### Decisiones tomadas

- MCP en biblioteca propia, no en ejecutable aparte: mantiene un único binario instalable.
- SQLite confirmado como store, con dos límites anotados en `docs/plan-arquitectura.md` §5.
- Inyección por constructor con composition root a mano; **sin contenedor de DI** en un CLI.
- El plan pasa de `notes/` (ignorado por git) a `docs/plan-arquitectura.md`.

### Siguiente paso concreto

`IGraphSink`: los cuatro bloques de exportación de `Program.cs` (~líneas 440-497) repiten
el cálculo del nombre de la BD y la derivación de extensión. Después, el paso **0.9**
(`ParserGeneral` escribiendo SQLite del grafo unificado), que es la prueba de que esta
arquitectura sirve para algo: si sale difícil, hay que revisarla antes de seguir a fase 1.

### Pendiente sin tocar

Actions en GitHub tras el primer push con CI real; `test/pr-impact-demo`; blog con 35
sustituciones aplicadas y sin commitear.
