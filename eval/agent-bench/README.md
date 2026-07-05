# agent-bench — benchmark de navegación de agentes sobre el nodestore

Mide cómo de bien un **modelo/agente** navega un nodestore — mismo protocolo para
cualquier modelo (Claude, DeepSeek, el que sea) y **cualquier base de datos**: los
seis tipos de caso son fijos, pero los sujetos y las respuestas esperadas se derivan
automáticamente del store que le apuntes. Qué mide: navegación + validez de salida
del agente (encontrar la respuesta precomputada y emitir JSON estricto), **no** la
corrección del motor (eso lo cubren los oráculos de la suite).

Todo es .NET — no hay Node ni Python en el camino.

## Tipos de caso (espejo de docs/nodestore-analysis.md)

| Caso | Tipo | Pregunta | Fichero que debería usar el agente |
| --- | --- | --- | --- |
| C1 | writes_of_object | qué tablas escribe X | change_map.json (impact.via_data) |
| C2 | transitive_callees | clausura de llamadas de X con profundidad y condicionalidad | change_map.json (impact.via_calls) |
| C3 | corpus_top_steps | top-3 objetos por total_steps | model.json |
| C4 | table_consumers | quién lee la tabla que X escribe | change_map.json (via_data.consumers) |
| C5 | column_roots | columnas raíz de una columna de salida | objects/<slug>/lineage_path.json |
| C6 | call_condition | bajo qué condición X alcanza a Y | change_map.json (condition_text) |

Si un store no tiene sujeto para un caso (p. ej. sin saltos condicionales), ese caso
se marca `skipped` y no puntúa.

## Uso

```powershell
# 1. Un nodestore de cualquier base (extract + pipeline --columns --nodestore)
# 2. Generar el banco (casos + prompts + esperados) desde ese store:
dotnet TSqlParser.dll bench-make <store.nodes> <bench_dir> [--seed N]

# 3. Ejecutar un modelo: darle cada cases/Cn.prompt.md (sustituyendo <STORE> por la
#    ruta real del store) y guardar sus salidas como answers/<modelo>/Cn.json.
#    Opcional: answers/<modelo>/run.json con metadatos libres
#    {"model": "...", "provider": "...", "date": "...", "engine_commit": "...", "tool_calls": N}

# 4. Puntuar (y acumular el scorecard en <bench_dir>/results/<modelo>.json):
dotnet TSqlParser.dll bench-grade <bench_dir> <bench_dir>/answers/<modelo>
```

- **`--seed N`**: rota qué sujeto elegible toma cada caso (`seed % candidatos`).
  Mismo seed ⇒ preguntas byte-idénticas (comparación justa entre modelos);
  seed distinto ⇒ otro juego de preguntas del mismo store, reproducible.
- El grader tolera BOM y vallas ```json, exige igualdad de conjuntos
  (case-insensitive) en listas, orden exacto en C3 y contención normalizada en C6.
- Exit codes de bench-grade: 0 todo PASS, 2 algún FAIL/MISSING/INVALID, 1 banco malformado.

## Protocolo por tipo de modelo

- **Agente con acceso a ficheros** (Claude Code subagente, etc.): recibe el prompt tal
  cual + la ruta del store; guarda él mismo sus Cn.json. Registrar tool calls en run.json.
- **Chat sin herramientas** (DeepSeek web/NIM, etc.): protocolo de relevo manual — pegas
  el prompt; cuando el modelo pida un fichero del store, se lo pegas; cuentas cada
  petición como un tool call. La respuesta final la guardas tú como Cn.json.

## Corpus demo

`sql/` contiene un micro-corpus (8 ficheros: cadena con salto condicional, vista con
lineage de columnas, tabla escrita con lector) que ejercita los 6 casos — útil como
smoke y para probar el protocolo. El banco de verdad se genera contra stores reales
(WWI, FRK, cliente). El autotest del arnés es `AgentBenchTests` en la suite xUnit.

## Resultados

Cada `bench-grade` escribe `results/<modelo>.json` (pass/graded, per_case, seed y los
metadatos de run.json). La tabla comparativa entre modelos es la colección de esos
ficheros para un mismo `<bench_dir>` (mismo store + mismo seed).
