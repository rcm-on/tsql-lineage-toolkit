# Experimento: Paso 1 (gate bad-practices a xUnit) con agente externo

Registro de la sesión de trabajo del **2026-07-03** (Claude Code / Fable 5 + Ramón).
Documenta el encargo a un agente externo para poder auditar su ejecución después.

## Configuración del experimento

| Campo | Valor |
|---|---|
| Fecha de encargo | 2026-07-03 (noche) |
| Agente ejecutor | **Cline** (modo agente en VS Code) |
| Modelo | **deepseek-ai/deepseek-v4-pro** vía **NVIDIA NIM (endpoint free)** |
| Alternativa evaluada y descartada | nvidia/nemotron-3-super-120b-a12b (se prefirió DeepSeek por linaje más fuerte en código/C#) |
| Tarea | **Solo el Paso 1** de [task-gates-dotnet.md](task-gates-dotnet.md): portar el gate de `eval/bad-practices` (evaluate.mjs + expected-findings.json) a un test xUnit in-process |
| Objetivo secundario | Medir si un NIM free sirve como ejecutor de trabajo mecánico con spec cerrada (decide si se le dan los pasos 2-4) |
| Presupuesto de paciencia | 3 iteraciones fallidas o ~45 min de supervisión; si se agota, terminar con Sonnet |

## Baseline pre-experimento (para aislar el diff del agente)

- HEAD: `49fef08` ("Add audit report + workflows to the nodestore")
- Suite: **97/97 en verde** (Paso 0 cerrado: flaky de timestamp arreglado —
  ver task-gates-dotnet.md §0)
- Dirty pre-existente (NO es del agente): `dashboard/index.html`,
  `dashboard/src/app.js`, `dashboard/src/components.js`, `dashboard/src/style.css`,
  `tests/TSqlParser.Tests/NodeStoreUpdateTests.cs` (fix del flaky) + untracked:
  `docs/task-gates-dotnet.md`, `docs/debate-artefactos-siempre-vs-opcionales.md`,
  `docs/debate-cline-brief.md`
- Copia también en scratchpad de la sesión: `baseline-pre-nim.txt`

## Prompt entregado a Cline (literal)

```
Tu tarea es ejecutar SOLO el "Paso 1" del documento docs/task-gates-dotnet.md
(léelo entero, incluida la sección "Invariantes"). El Paso 0 ya está cerrado: ignóralo.

CONTEXTO QUE DEBES LEER ANTES DE ESCRIBIR CÓDIGO:
- docs/task-gates-dotnet.md (la spec: Paso 1 + Invariantes)
- eval/bad-practices/evaluate.mjs (el comparador Node que vas a portar a C#)
- eval/bad-practices/expected-findings.json (el oráculo; PROHIBIDO modificarlo)
- eval/bad-practices/sql/ (el corpus de entrada)
- tests/TSqlParser.Tests/NodeStoreUpdateTests.cs (referencia de convenciones:
  cómo construir el análisis in-process con BuildGraph/GraphExporter.Build,
  JsonOptions, estilo de aserciones)

ENTREGABLE:
1. Un nuevo fichero de test xUnit en tests/TSqlParser.Tests/ (p.ej.
   BadPracticesGateTests.cs) que: analiza el corpus sql/ IN-PROCESS (mismas
   clases que usa Program.cs — NO ejecutar el DLL como proceso externo),
   detecta los hallazgos, y los compara contra expected-findings.json.
   Debe fallar con mensaje claro si FALTAN hallazgos, si SOBRAN, o si no
   coincide severidad/categoría. Referencia de resultado esperado: OK=38
   FALTAN=0 SOBRAN=0.
2. Una bitácora de ejecución en docs/agent-run-paso1-deepseek.md donde registres:
   tu plan inicial, CADA comando que ejecutes con su salida REAL pegada
   (no resumida), cada intento fallido y por qué falló, y la salida final
   completa de `dotnet test`. Regla dura del proyecto: una afirmación sin
   salida de comando pegada no cuenta como hecha.

PROHIBIDO:
- Tocar src/TSqlParser/ (ni una línea)
- Tocar expected-findings.json
- Tocar o borrar tests existentes
- Tocar eval/bad-practices/*.mjs, run.sh, run.ps1 (se deprecan después, no ahora)
- Hacer commit o push

CRITERIO DE ÉXITO (verifícalo tú y pega la prueba en la bitácora):
- `dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release`
  todo en verde, con más de los 97 tests actuales.
- El test nuevo reproduce el resultado del gate Node: 38 hallazgos esperados,
  0 faltan, 0 sobran.
```

## Protocolo de auditoría posterior (lo ejecuta Claude al volver Ramón)

1. **Diff contra baseline** — solo `tests/` + su bitácora; nada en
   `src/TSqlParser/`, `expected-findings.json` ni tests existentes.
2. **Suite completa** — verde, conteo > 97.
3. **Prueba de mutación** (⚠️ NO revelada a Cline, es control externo a ciegas):
   corromper temporalmente un expected → el test nuevo DEBE fallar; revertir.
4. **Calidad de código:** ¿in-process o spawnea el DLL (anti-objetivo)?
   ¿aserciones a hallazgo concreto o solo conteos? ¿convenciones del proyecto?
   ¿reporta FALTAN/SOBRAN con claridad?
5. **Auditoría de proceso** sobre `docs/agent-run-paso1-deepseek.md` (+ log de
   la sesión de Cline si Ramón lo guarda): iteraciones, afirmaciones sin salida
   pegada (narración), diffs fallidos. Veredicto comparativo honesto y decisión
   sobre pasos 2-4.

## Estado

- [x] Prompt entregado a Ramón para Cline
- [x] Ejecución de Cline/DeepSeek — **DETENIDA** (ver veredicto)
- [x] Auditoría (de proceso; no hubo entregable de código que auditar)
- [x] Veredicto y decisión sobre pasos 2-4

## Veredicto (2026-07-03, experimento cerrado sin código entregado)

**Cronología observada:** (1) exploración disciplinada — leyó spec, oráculo,
comparador, convenciones y amplió a Program/GraphExporter/Models/SqlAnalyzer
con criterio; (2) un tropiezo de shell (`dir /b` de cmd en PowerShell);
(3) **límite de contexto** del endpoint NIM free → re-lecturas (context
thrashing); (4) pese a ello, **análisis final correcto**: detectó que
`evaluate.mjs` ejecuta las reglas del DASHBOARD (`SD.shape`+`SD.risks.analyze`
vía window simulado), que `AuditExporter` emite otros patrones, y que por tanto
la tarea "in-process en C#" exigía una decisión de arquitectura no cubierta por
la spec. Se quedó razonando ahí (sin contexto para más) y se detuvo el
experimento.

**Conclusiones:**

1. **La spec del Paso 1 tenía un defecto** (asumía las reglas en C#) — el
   agente lo encontró y Claude lo VERIFICÓ contra `evaluate.mjs:32-51`. Paso 1
   re-alcanzado en `task-gates-dotnet.md` (opciones A/B/C, recomendada A).
2. **DeepSeek-v4-pro (NIM free): calidad de análisis alta, viabilidad agéntica
   baja en este repo.** No alucinó, no hackeó una solución inválida, se paró en
   la decisión correcta — pero el contexto del endpoint free no soporta el modo
   agente autónomo sobre un repo de este tamaño.
3. **Decisión pasos 2-4:** no asignar a NIM free en modo agente autónomo.
   Uso viable residual: generación de fichero completo con contexto mínimo
   curado a mano ("modo generador"), no probado en este experimento.
4. La opción A (promover reglas a C#) es **trabajo de motor** → modelo fuerte
   (Fable/Sonnet), conforme al reparto por capacidades.
