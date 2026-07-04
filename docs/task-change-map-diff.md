# Tarea: diff de change_maps — el caso PR ("¿qué rompe este cambio?")

**Estado:** SPEC (Fable, 2026-07-04). Depende de: change_map.json v1 (HECHO,
`b080ef2`, ver `task-change-map.md`). Ejecutor previsto: Fable u Opus (motor +
CLI); la GitHub Action envolvente puede bajar a Sonnet.

## Objetivo

Dado el nodestore ANTES y el nodestore DESPUÉS de un cambio (p. ej. rama base vs
rama del PR), emitir un `change_map_diff.json` que responda en una lectura:
**qué objetos cambiaron, qué impacto ganaron/perdieron y a quién afecta ahora
que antes no**. Es el sustrato del comentario automático en PRs — el caso de
negocio más vendible del toolkit — y del futuro tool MCP `diff_impact`.

## CLI

```
dotnet TSqlParser.dll diff-change-map <store_antes.nodes> <store_despues.nodes> <salida.json> [--fail-on-new-impact]
```

- Lee SOLO `manifest.json` (content_hash por objeto) y `change_map.json` de cada
  store — no re-analiza SQL. Barato por diseño.
- `--fail-on-new-impact`: exit ≠ 0 si hay impacto nuevo (gate de CI opcional).

## Formato de salida (propuesta v1)

```json
{
  "objects_changed":   ["Db::Schema.Proc"],      // content_hash distinto
  "objects_added":     [],
  "objects_removed":   [],
  "impact_delta": {
    "Db::Schema.Proc": {
      "via_calls_added":   [{ "object": "...", "depth": 1, "conditional": false }],
      "via_calls_removed": [],
      "via_data_added":    [{ "table": "...", "consumers": ["..."] }],
      "via_data_removed":  [],
      "newly_affected":    ["Integration.GetOrderUpdates"]   // consumidores que antes NO dependían de nada tocado
    }
  },
  "workflows_delta": {
    "added":   ["entry_name"],
    "removed": [],
    "reshaped": [{ "entry": "...", "paths_before": 3, "paths_after": 4 }]
  },
  "summary": { "changed": 1, "newly_affected_total": 3, "risk_note": "impacta sync externo (Integration.*)" }
}
```

Reglas: solo se listan deltas (objeto sin cambios no aparece); `newly_affected`
es la unión de consumidores/callees nuevos — la línea que va al comentario del
PR. Sin timestamp propio (lección del flaky).

## Decisiones tomadas (no re-litigar en implementación)

1. **Diff de artefactos, no de SQL:** comparar change_maps ya generados. Quien
   quiera el diff genera los dos stores primero (en CI: base ya cacheado o
   regenerado; el coste de generar es de segundos, medido).
2. **`objects_changed` sale de `manifest.json.content_hash`**, no de heurísticas.
3. **Identidad por id de objeto** (`Db::Schema.Name`); renames = removed+added
   (v1 no detecta renames; anotar en summary si added y removed comparten
   via_data muy similar — stretch, no bloqueante).
4. Implementación como `ChangeMapDiff.cs` + subcomando en Program.cs; tests
   unitarios con dos stores sintéticos generados in-process (patrón
   ChangeMapTests.WriteAndUpdate).

## Tests previstos

1. Cambio que añade una escritura → `via_data_added` + `newly_affected`.
2. Cambio que añade un EXEC → `via_calls_added` con profundidad.
3. Objeto nuevo / borrado → added/removed, sin falsos deltas en el resto.
4. Sin cambios → diff vacío, exit 0 incluso con `--fail-on-new-impact`.
5. `--fail-on-new-impact` con impacto nuevo → exit ≠ 0.

## Después (fuera de esta tarea)

- GitHub Action: job que en cada PR regenera el store de la rama, hace el diff
  contra el de base y comenta el `summary` + `newly_affected` en el PR (Sonnet).
- MCP tool `diff_impact` sirviendo el mismo JSON.
