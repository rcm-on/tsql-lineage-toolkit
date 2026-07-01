# Spec: modelar objetos creados dinámicamente (triggers) como nodos con relaciones

**Estado:** PROPUESTA (borrador de Claude, 2026-07-01). Pendiente de revisión de Gemini
antes de implementar. Toda la evidencia de abajo está **medida sobre el pipeline real**, no
estimada — ver comandos reproducibles al final.

## 1. Problema

`DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad` (WWI) arma y ejecuta 34 sentencias
de SQL dinámico. Tras cerrar los gaps 5.1/5.2 (`extraction-gaps.md`), las 34 resuelven a texto
literal — 17 `DROP TRIGGER` + 17 `CREATE TRIGGER`. Pero ese texto es **solo descriptivo**: el
grafo NO sabe qué hace cada trigger.

Medido sobre `out/graph_full.json`:
- El proc tiene `WRITES_TO = 34` → son los `ALTER TABLE … SET SYSTEM_VERSIONING = OFF` / `DROP
  PERIOD` estáticos sobre las **17 tablas base** (Cities, Countries, People, Customers…).
- **Ninguna tabla `*_Archive`** aparece como destino, aunque el cuerpo de cada trigger hace
  `INSERT INTO [Schema].[Table_Archive] (…) SELECT … FROM inserted/deleted`.
- **0 nodos de tipo Trigger** en el grafo.

**Causa:** `SqlAnalyzer.ResolveDynamicSqlLinks` sí re-parsea el SQL dinámico resuelto a aristas,
pero solo extrae **DML** (`INSERT/UPDATE/DELETE/SELECT/MERGE`, `SqlAnalyzer.cs:151-153`). Un
`CREATE TRIGGER` es DDL → `dml.Count == 0 → continue`. Se ignora.

## 2. Objetivo (qué debe poder responder el agente)

Dado "voy a tocar la tabla `Application.Cities`":
1. **Qué trigger salta** (`TR_Application_Cities_DataLoad_Modify`) y ante qué evento
   (`AFTER INSERT, UPDATE`).
2. **Sobre quién actúa** su cuerpo: escribe en `Application.Cities_Archive` (lineage de tabla)
   y, si se puede, con qué columnas (lineage de columna `Cities_Archive.X <- Cities.X` vía
   `inserted`).
3. Que ese impacto quede **desacoplado del proc**: el proc solo CREA el trigger; el `INSERT`
   ocurre después, cuando alguien modifica la tabla base — no cuando corre el proc.

## 3. Semántica: el trigger es su propio nodo, no un WRITES_TO del proc

Punto de diseño **crítico** (y la razón de que hoy NO se modele mal): el cuerpo del trigger no
se ejecuta durante el proc. Atribuir `proc —WRITES_TO→ Cities_Archive` sería lineage
temporalmente incorrecto. La modelización correcta introduce el trigger como **objeto de
primera clase**:

```
proc  —CREATES→  Trigger(TR_Application_Cities_DataLoad_Modify)
Trigger  —ON→  Table(Application.Cities)            (tabla que lo dispara)
Trigger  —FIRES_ON→ {INSERT, UPDATE}                (evento; propiedad o arista)
Trigger  —WRITES_TO→ Table(Application.Cities_Archive)
Trigger  —READS_FROM→ inserted/deleted (→ Table Application.Cities)
Trigger  —DERIVES_FROM (nivel columna, si se logra): Cities_Archive.Col <- Cities.Col
```

Así, el impacto se consulta navegando `Cities —(triggers que la tienen como ON)→ Trigger
—WRITES_TO→ Archive`, sin ensuciar el lineage del proc que lo creó.

## 4. Restricción técnica descubierta (bloqueante): truncado a 200

`ResolveExecLiteral` devuelve `SqlText.Truncate(collapsed, 200)` (`AstWalker.cs:1771`). Ese
mismo valor truncado es `FlowLinkInfo.DynamicSqlText`, que se usa **a la vez** para:
- el campo de display `dynamic_sql` (`GraphExporter.cs:324`), y
- el input de re-parseo de `ResolveDynamicSqlLinks` (`SqlAnalyzer.cs:145`).

Un cuerpo de `CREATE TRIGGER` real supera con creces 200 chars, así que re-parsearlo sobre
`DynamicSqlText` fallaría (texto cortado → `errors.Count > 0 → continue`). **La spec exige
separar los dos usos:**
- `DynamicSqlText` (display) sigue truncado a 200.
- Un campo/paso nuevo conserva el **texto resuelto completo** (o se re-resuelve sin truncar) SOLO
  para el re-parseo estructural. Candidato: guardar el literal completo en el `WalkContext`
  al resolver el EXEC, o hacer que `ResolveExecLiteral` exponga una variante sin truncar.

> Nota colateral: esto significa que el re-parseo DML actual (`ResolveDynamicSqlLinks`) también
> está silenciosamente limitado a dynamic SQL < 200 chars. Vale la pena un caso de prueba que lo
> confirme/regule aparte de esta feature.

## 5. Alcance propuesto (incremental, para no morder demasiado de una vez)

- **Fase A — nodo Trigger + CREATES + ON + evento.** Re-parsear el `CREATE TRIGGER` resuelto
  (texto completo) a: nodo Trigger, arista `proc —CREATES→ Trigger`, arista `Trigger —ON→
  Table`, evento como propiedad. Sin lineage del cuerpo todavía. Esto ya responde "qué trigger
  y sobre qué tabla" como **grafo**, no solo texto.
- **Fase B — WRITES_TO/READS_FROM del cuerpo.** Caminar el cuerpo del trigger (que es
  `StatementList` normal una vez parseado) con el `AstWalker` existente, atribuyendo sus
  FlowLinks al nodo Trigger (no al proc). Reutiliza toda la maquinaria de lineage de tabla.
- **Fase C — lineage de columna del cuerpo** (`Cities_Archive.Col <- Cities.Col`). Depende de
  resolver `inserted`/`deleted` como alias de la tabla base — más delicado, se decide tras B.

Cada fase con su caso en `eval/community-edge-cases/` y su gate.

## 6. Preguntas abiertas para Gemini

1. ¿`inserted`/`deleted` se modelan como pseudo-tablas propias o se resuelven directamente a la
   tabla base para el lineage? (afecta a Fase C).
2. ¿El nodo Trigger es un `SqlObject` normal (aparece en `model.json`, tiene su `objects/<slug>/`
   en el nodestore) o un tipo más ligero? Un trigger creado dinámicamente no tiene fichero
   fuente propio — ¿de dónde cuelga su `object.json`?
3. ¿`CREATES` es un tipo de arista nuevo en `NavEdgeTypes` (para que la navegación del nodestore
   lo siga) o queda fuera de la navegación por defecto?
4. Estabilidad de IDs: el ID del trigger sale de texto resuelto en runtime del parser — ¿cómo
   garantizamos que sea determinista y estable entre corridas (invariante que ya acordamos)?

## 7. Reproducir la evidencia

```bash
# desde tsql-lineage-toolkit/, con out/ ya regenerado (--columns --nodestore)
# 1) el proc NO conoce ninguna tabla *_Archive, solo las 17 base vía ALTER:
node -e "const d=require('./out/graph_full.json');const p='WideWorldImporters::DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad';const ids=new Set(d.nodes.filter(n=>n.id.startsWith(p)).map(n=>n.id));const by={};d.nodes.forEach(n=>by[n.id]=n);const t=[...new Set(d.relationships.filter(r=>ids.has(r.source)).map(r=>by[r.target]).filter(n=>n&&n.labels.includes('Table')).map(n=>n.properties.full_name))];console.log('Archive?',t.filter(x=>/archive/i.test(x)),'\\ntotal tablas:',t.length)"
# 2) el texto del CREATE TRIGGER resuelto está truncado a 200:
node -e "const d=require('./out/graph_full.json');const s=d.nodes.find(n=>/CREATE TRIGGER/.test(n.properties?.dynamic_sql||''));console.log(s.properties.dynamic_sql.length,'chars')"
```
