# Brief autocontenido para Cline — decisión de diseño

> **Cline: NO abras ningún otro fichero.** Todo lo que necesitas para decidir está
> aquí. Responde escribiendo al final de este mismo fichero, firmando `[Cline]`.
> Objetivo: decisión con criterio que cree valor, no ganar la discusión.

---

## La pregunta

¿`model.json` y `audit_report.json` (dos ficheros que produce el toolkit) deben
**generarse siempre**, o ser **opcionales** (detrás de un flag)?

## Contexto mínimo (todo inline, no busques fuera)

**Qué es el toolkit:** motor de impacto/lineage de T-SQL para decisión asistida por
LLM. Dado "voy a tocar esta tabla/proc", dice qué se ve afectado y en qué orden
arreglarlo. La **completitud de la extracción es la prioridad nº1**.

**Los dos ficheros en disputa** (viven en `out/graph_full.nodes/`):
- `model.json` = **índice/mapa** del nodestore: objetos + tablas + quién llama/lee/
  escribe a quién + workflows. Todo consumidor (dashboard, agente) arranca por aquí.
- `audit_report.json` = **capa de análisis**: hotspots con score de riesgo, blind
  spots, patrones de riesgo, e **impacto precomputado** (qué se rompe si tocas X).

**HECHO del código (no opinión):** hoy los dos se escriben **siempre** que se pasa
`--nodestore` (`NodeStoreExporter.cs:293` y `:296`), sin flag.

**PRECEDENTE del propio repo:** para un tercer fichero parecido (`change_map.json`)
ya se decidió *"sin flag nuevo — se genera siempre con `--nodestore`. Sin él un
agente no puede responder preguntas de impacto sin abrir todos los object.json."*
→ Este debate debe **ratificar** ese criterio o dar una razón fuerte para romperlo.

**DATO de valor:** existe un benchmark que compara un agente **ciego** (solo el
grafo crudo) contra uno **informado** (con estos precomputados). El informado gana
sobre todo por el impacto/hotspots ya calculados. O sea: `audit_report.json` es la
capa que más valor aporta al agente.

---

## Las dos posturas (resumidas)

**Postura A — "siempre" (la de Claude):**
1. Coste de generar ≈ 0: se calculan sobre un grafo que ya está en RAM. El clásico
   "es caro, que lo pida con flag" **no aplica** porque no hay coste.
2. `model.json` es el manifiesto: hacerlo opcional = "medio nodestore" y obliga a
   cada consumidor a llevar código de fallback (como hacer `package.json` opcional).
3. Generarlos **atómicos**: un `audit` opcional que queda *stale* respecto al
   `model` es peor que no tenerlo (da confianza falsa).
4. Lo "opcional" correcto es la **carga** (no leas lo que no uses) y la
   **verificación** (`--verify-audit`, que ya es opt-in) — no la generación.

**Postura B — "opcional" (steelman, para que la ataques o la defiendas):**
1. `model.json` es un *hecho*; `audit_report.json` es *opinión con umbrales*
   (¿score 462 = hotspot? depende de una constante). Un flag marcaría esa frontera.
2. Cada campo que emites siempre es un campo que no puedes cambiar sin romper
   consumidores. Opcional = libertad para iterar el formato del audit.
3. Consumidores que solo hacen lineage estructural no quieren cargar/versionar
   500 KB de risk patterns.
4. Hoy el audit es barato; si mañana necesita datos de runtime (DMVs), se vuelve
   caro y *ahí* querr