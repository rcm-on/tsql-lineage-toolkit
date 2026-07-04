# Debate — ¿`model.json` y `audit_report.json` opcionales o siempre?

> Doc de decisión compartido **Claude ⇄ Cline**. Mismas reglas que
> `agent-collab.md`: lee todo antes de escribir, firma con `[Claude]` / `[Cline]`,
> **no reescribas la sección del otro — añade debajo**. Objetivo explícito de este
> debate: **tomar una decisión con criterio y que cree valor real**, no ganar la
> discusión.

---

## Arranque en frío — contexto e instrucciones para el agente

**Si llegas frío a este fichero, lee esto antes de opinar. No hace falta que leas
todo el repo para participar; sí que entiendas estas cinco cosas.**

**Qué es el toolkit.** `tsql-lineage-toolkit` es un motor de **impacto/lineage de
T-SQL** para decisión asistida por LLM: dado un cambio ("voy a tocar esta tabla /
este proc"), responde qué se ve afectado, en qué orden remediarlo, y da una visión
macro del sistema. **La completitud de la extracción es la prioridad nº1 de
solidez** — todo lo demás se subordina a eso.

**Qué son los dos artefactos del debate** (ambos viven en `out/graph_full.nodes/`):
- `model.json` — el **manifiesto/índice** del nodestore. Nodos SqlObject+Table,
  edges a nivel objeto (CALLS/READS_FROM/WRITES_TO/AFFECTS) y los *workflows*
  precomputados. Es por donde arranca **todo** consumidor (dashboard y agente).
- `audit_report.json` — la **capa de análisis**: hotspots (score de riesgo),
  blind spots, risk patterns, cobertura de lineage e **impacto precomputado** (blast
  radius vía llamadas + vía datos) por objeto.

**Entorno (Windows 11), por si necesitas reproducir algo:**

- Build: `dotnet build src/TSqlParser/TSqlParser.csproj -c Release` (.NET 10, ScriptDOM).
  Binario en `src/TSqlParser/bin/Release/net10.0/TSqlParser.dll`.
- ⚠️ `dotnet run`/`dotnet test` pueden fallar por SAC (`0x800711C7`); **invocar el
  DLL directamente con `dotnet <ruta>.dll ...` SÍ funciona**.
- **No hay Python.** Sí hay **Node v24** y **sqlcmd**. Oráculo de validación:
  `localhost\SQLEXPRESS` (auth Windows) con WideWorldImporters + AdventureWorks2019.
- Pipeline: `dotnet <DLL> input.json graph_full.json --columns --nodestore`
  genera grafo + nodestore (y hoy, siempre, los dos artefactos de este debate).

**Evidencia ya disponible que puedes citar (no reinventes la medición):**

- `docs/agent-collab.md` — bitácora Claude⇄Gemini; contiene el **precedente
  `change_map.json`** ("sin flag, siempre") que este debate debe ratificar o romper.
- `docs/coverage-matrix.md` — qué extrae el motor y con qué oráculo.
- `eval/agent-benchmark/benchmark.md` — el experimento agente-ciego (Track A) vs
  agente-informado (Track B) que mide el valor del audit precomputado.

**Instrucciones para participar (protocolo del debate):**

1. **No valides sin más.** El valor de este doc es la fricción: ataca donde el
   argumento del otro esté más flojo. Un "estoy de acuerdo" sin contra-argumento no
   mueve la decisión.
2. **Basa las afirmaciones en evidencia**, no en intuición: cita fichero:línea, un
   número del benchmark, o el precedente. Si es una suposición, márcala como tal.
3. **Firma cada bloque** con `[Claude]` o `[Cline]`. **No reescribas la sección del
   otro; añade debajo.** Las secciones §3–§5 son de apertura y quedan congeladas.
4. **Rellena tu voto en la rúbrica de §7** con una línea de justificación por
   criterio. Sin rúbrica puntuada, no hay decisión, solo opiniones.
5. **Regla de cierre:** cuando ambos votos estén y la rúbrica converja, se escribe
   la decisión final (la valida el usuario). Si los votos **divergen**, se escala al
   usuario **el punto exacto de desacuerdo**, no el debate entero.

---

## 0. La pregunta (planteada por el usuario)

> ¿Tiene sentido, de forma profesional, que `audit_report.json` y `model.json`
> sean **opcionales** (detrás de un flag / carga bajo demanda) o que **siempre**
> se generen y estén disponibles?

**Estado actual del código (hecho verificado, no opinión):** ambos se escriben
**incondicionalmente** cuando se pasa `--nodestore`:

- `NodeStoreExporter.cs:293` → `model.json`
- `NodeStoreExporter.cs:296` → `audit_report.json`
- Idem en `Update()` (`:354`, `:357`).

**Precedente interno relevante (no re-litigar sin motivo):** en `agent-collab.md`,
la decisión de `change_map.json` (P7) fue explícita: *"Sin flag nuevo — se genera
siempre con `--nodestore`. Sin él un agente no puede responder preguntas de impacto
sin abrir todos los `object.json`."* Cualquier conclusión de este debate que haga
`audit_report.json` opcional entra en **contradicción con ese precedente** y debe
justificar por qué son casos distintos.

---

## 1. Qué es cada artefacto (para no debatir sobre cosas distintas)

| Artefacto | Naturaleza | Coste de generar | Quién lo consume primero |
|---|---|---|---|
| `model.json` | **Índice/manifiesto** del nodestore: nodos SqlObject+Table, edges objeto-nivel, workflows | 1 pasada sobre el grafo ya en memoria | Todo consumidor (dashboard, agente) arranca por aquí |
| `audit_report.json` | **Capa de análisis/opinión**: hotspots, blind spots, risk patterns, impacto precomputado | 1 pasada in-memory sobre el mismo grafo | Agente de impacto; dashboard de riesgos |

Diferencia que importa: `model.json` es **dato estructural** (un hecho: quién llama
a quién). `audit_report.json` es **dato derivado con umbrales** (una opinión:
"esto es un hotspot con score 462"). Esa diferencia es la única grieta por la que
el bando "opcional" puede meter cuña — la exploto en §4.

---

## 2. El error de encuadre que hay que evitar

La pregunta "opcional vs siempre" mezcla **dos ejes independientes**. Separarlos
es el 80% de la decisión:

- **Eje GENERACIÓN (write-time):** ¿el exporter *escribe* el fichero siempre, o
  solo con un flag?
- **Eje CARGA (read-time):** ¿el consumidor *lee* el fichero siempre al arrancar,
  o bajo demanda?

Casi toda la intuición legítima detrás de "hazlo opcional" pertenece al eje de
**carga** (no cargues 2 MB de audit si solo quieres el mapa). Casi todo el riesgo
de "hazlo opcional" aparece en el eje de **generación** (medio nodestore que
rompe a los consumidores). Confundirlos lleva a la peor decisión posible: gatear
la *generación* para ahorrar un coste que en realidad era de *carga*.

---

## 3. Posición de apertura `[Claude]`

**Tesis: generar siempre y de forma atómica; cargar perezoso; verificar opt-in.**

**3.1 Generación → SIEMPRE, para ambos.**
- Coste marginal ≈ 0: se computan sobre un grafo que ya está en RAM en la misma
  invocación. No hay round-trip a BD ni I/O caro. El argumento clásico pro-flag
  ("es caro, que el usuario lo pida") **no aplica aquí** porque el coste no existe.
- `model.json` es el manifiesto. `index.json.howto` *promete* que es la vista
  completa de arranque. Hacerlo opcional produce un "medio nodestore" y obliga a
  **cada** consumidor a llevar código de fallback. Es hacer `package.json`
  opcional: técnicamente posible, organizativamente un generador de bugs.
- `audit_report.json` es justo la capa de **mayor valor para el agente**. El
  propio `eval/agent-benchmark/` existe para medir que Track B (informado) gana a
  Track A (ciego) *precisamente* por el impacto/hotspots precomputados. Gatear esa
  capa es gatear el valor diferencial del producto.

**3.2 Atomicidad → los dos juntos o ninguno.**
- El peor estado no es "falta audit"; es "`audit_report.json` describe una versión
  de `model.json` que ya cambió". Un audit opcional que se puede quedar stale
  respecto al model es **peor que no tenerlo**: da confianza falsa. Regla:
  regenerar el nodestore es una operación atómica sobre todos sus artefactos.

**3.3 Carga → perezosa (aquí "opcional" sí es correcto).**
- El consumidor carga `model.json` primero (el mapa) y `audit_report.json` /
  `object.json` / `lineage_path.json` **bajo demanda**. Eso ya es el diseño del
  benchmark. "Opcional" vive en el lado del lector, no del productor.

**3.4 Lo que SÍ debe ser opt-in (y ya lo es, correctamente): `--verify-audit`.**
- Verificar invariantes (`AuditVerifier.cs`) es una preocupación de **CI/gate**,
  no de producción de datos. Ese es el flag correcto para hacer opcional, y ya se
  hizo así (`Program.cs`). Sirve de contra-ejemplo útil: demuestra que el equipo
  ya sabe distinguir "dato que siempre se produce" de "chequeo que se pide".

**3.5 Criterio de valor.** La decisión "siempre" maximiza valor porque:
(a) reduce la superficie de estados a testear (no hay matriz "con/sin audit");
(b) da a cualquier agente una garantía de completitud sobre la que puede
programar sin ramas defensivas; (c) alinea con el precedente de `change_map.json`
→ coherencia del formato del nodestore como contrato estable.

---

## 4. Steelman del bando contrario (lo argumento yo para que el debate sea real) `[Claude]`

No quiero que Cline tenga que pelear contra un espantapájaros. El mejor caso
"hazlo opcional en generación" es:

- **A. Separación de responsabilidades.** `model.json` es *hecho*;
  `audit_report.json` es *opinión con umbrales* (¿score 462 es "hotspot"? depende
  de una constante). Mezclar la capa de política en el artefacto base acopla el
  formato del nodestore a decisiones de scoring que pueden cambiar más rápido. Un
  flag `--audit` marcaría esa frontera: "el nodestore estructural es estable; el
  informe de opinión es una feature aparte".
- **B. Coste de mantenimiento del contrato.** Cada campo que se emite siempre es
  un campo que **no puedes cambiar sin romper consumidores**. Hacer opcional el
  audit deja libertad para iterar su forma sin versionar el nodestore entero.
- **C. Ruido para consumidores que no lo usan.** Un consumidor que solo hace
  lineage estructural igual no quiere 500 KB de risk patterns en su árbol de
  salida (git diffs, tamaño de artefacto en CI).
- **D. Deriva futura de coste.** Hoy el audit es in-memory barato. Si mañana el
  scoring necesita datos de runtime (frecuencia de ejecución real, tamaños de
  tabla vía DMVs), pasa a ser caro y *entonces* querrás gatearlo — y migrar de
  "siempre" a "opcional" rompe a quien ya dependía de que estuviera.

**Mi réplica breve a mi propio steelman** (para dejar el hueco donde Cline puede
empujar): A y B son argumentos sobre **estabilidad del contrato**, no sobre
generación — se resuelven versionando el *schema* del audit (`audit_report.json`
con su propio `schema_version`), no ausentándolo. C es un problema de **carga**,
no de generación (no lo leas). D es real pero es un *trigger de reclasificación
futura*, no un motivo hoy: el día que el audit toque la BD, deja de ser el mismo
artefacto y ahí sí nace el flag. Convertir hoy por un coste hipotético de mañana
es pagar complejidad por adelantado.

---

## 5. Criterios de evaluación (para decidir con método, no por gusto)

Puntuar cada opción 1–5 por criterio. Pesos según el objetivo declarado (valor +
solidez del motor, que es la prioridad nº1 del toolkit).

| # | Criterio | Peso | Por qué importa aquí |
|---|---|---|---|
| C1 | **Valor para el agente** | ×3 | Es la razón de ser del toolkit (impacto/decisión LLM) |
| C2 | **Solidez / superficie de estados a testear** | ×3 | Menos ramas = menos bugs silenciosos en refactor (eje B) |
| C3 | **Coherencia del contrato del nodestore** | ×2 | Consumidores programan contra una garantía estable |
| C4 | **Coste real (CPU/IO/tamaño)** | ×1 | Hoy ≈0; solo pesa si cambia la naturaleza del audit |
| C5 | **Flexibilidad para iterar el formato** | ×2 | No congelar decisiones de scoring prematuramente |

Opciones a puntuar:
- **OP-1** Generar siempre ambos + carga perezosa + `schema_version` en audit.
- **OP-2** `model.json` siempre; `audit_report.json` detrás de `--audit`.
- **OP-3** Ambos detrás de flags (opt-in total).

*(Rúbrica a rellenar entre los dos en §7.)*

---

## 6. Turno de Cline `[Cline]`

Cline: por favor, no valides sin más. Ataca donde el argumento de Claude está más
flojo. Puntos concretos donde quiero presión:

1. ¿El precedente de `change_map.json` (§0) realmente aplica a `audit_report.json`,
   o son distintos porque uno es navegación y otro es *scoring con umbrales*?
2. El argumento **B** del steelman (§4): ¿basta `schema_version` para desacoplar el
   contrato, o eso es teoría y en la práctica los consumidores se acoplan igual?
3. ¿Hay un consumidor real (dashboard, CI, agente) al que hoy le *estorbe* que el
   audit se genere siempre? Si no existe, C3/C4 del bando contrario son hipotéticos.
4. Tu voto en la rúbrica de §7, con una línea de justificación por criterio.

Escribe tu respuesta **debajo de esta línea**, firmada `[Cline]`. No edites §3–§5.

---

## 7. Rúbrica de decisión (rellenar entre los dos)

| Criterio (peso) | OP-1 siempre+lazy | OP-2 audit opt-in | OP-3 todo opt-in |
|---|---|---|---|
| C1 Valor agente (×3) | 5 → 15 | 3 → 9 | 1 → 3 |
| C2 Solidez/estados (×3) | 5 → 15 | 3 → 9 | 2 → 6 |
| C3 Coherencia contrato (×2) | 5 → 10 | 3 → 6 | 2 → 4 |
| C4 Coste real (×1) | 4 → 4 | 4 → 4 | 5 → 5 |
| C5 Flexibilidad formato (×2) | 4 → 8 | 5 → 10 | 5 → 10 |
| **Total ponderado** | **52** | **38** | **28** |

*Lectura:* OP-1 solo pierde en C5 (flexibilidad), y ese hueco se cierra con
`schema_version` sin renunciar a C1/C2/C3. El coste (C4) es un empate práctico
—generar es ≈0— así que su ×1 no mueve nada. OP-3 gana C4/C5 pero hunde el valor
(C1) y la coherencia, que es justo la razón de ser del toolkit.

**Voto Claude:** OP-1 (siempre + lazy + `schema_version`). Es la única que puntúa
alto en C1/C2/C3 sin perder C5 (el versionado del schema recupera la flexibilidad
sin pagar el coste de estado de OP-2/OP-3). C4 es neutro hoy.

**Voto Cline:** *(delegado — el usuario, ante el bloqueo de contexto de Cline, delegó
la decisión en Claude el 2026-07-02; el brief autocontenido `debate-cline-brief.md`
queda disponible por si Cline quiere impugnar a posteriori).*

---

## 8. DECISIÓN FINAL `[Claude]` — cerrada 2026-07-02

**Resuelto: OP-1. Generar `model.json` y `audit_report.json` SIEMPRE con `--nodestore`,
de forma atómica; la opcionalidad vive en la CARGA (perezosa) y en la VERIFICACIÓN
(`--verify-audit`, opt-in), no en la generación.** Rúbrica: 52 vs 38 vs 28.

**Por qué (una línea por criterio ganador):**

- **C1/C2/C3** — el nodestore es un *contrato de completitud*: un consumidor programa
  contra "estos ficheros siempre están", sin ramas de fallback. Eso es el valor.
- Ratifica el precedente de `change_map.json` ("sin flag, siempre") → **coherencia
  del formato**, no una excepción caso por caso.
- El único punto real del bando "opcional" (iterar el formato del audit sin romper
  consumidores, argumento B/§4) se resuelve con **`schema_version` dentro de
  `audit_report.json`**, no ausentando el fichero.

**Condición de reapertura (cuándo esta decisión deja de valer):** si `audit_report.json`
pasa a necesitar datos de **runtime** (DMVs, frecuencia de ejecución, tamaños reales) y
deja de ser un cómputo in-memory barato, entonces cambia de clase de artefacto y **ahí
sí** nace un flag `--audit`. No antes.

**Implicación de trabajo (lo único accionable que sale del debate):** añadir un campo
`schema_version` a `audit_report.json` (y su chequeo en `AuditVerifier`). Todo lo demás
ya está como decide OP-1 — no hay que tocar la generación.

**Estado verificado hoy (2026-07-02):** build Release OK; `update-nodestore ... --verify-audit`
→ 64 objetos idempotentes, `[verify-audit] OK`, exit 0. La decisión no rompe nada porque
ratifica el comportamiento ya existente.
