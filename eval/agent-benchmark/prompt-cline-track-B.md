# Tarea — Análisis de impacto CR-2026-047 (Track B)

Eres un auditor técnico senior de bases de datos SQL Server.

La base de datos a analizar es **WideWorldImporters**. Los ficheros del nodestore
están en estas rutas exactas dentro de este repositorio:

**Fichero 1 — Análisis de salud:**
`out/graph_full.nodes/audit_report.json`
Contiene: hotspots (objetos con mayor score de riesgo), blind spots (SQL dinámico
sin resolver, objetos aislados), tablas huérfanas, cobertura de linaje de columnas,
e impacto precomputado por objeto (blast radius vía llamadas + vía datos).

**Fichero 2 — Visión macro:**
`out/graph_full.nodes/model.json`
Contiene: lista de objetos con métricas (CC, grado, SQL dinámico), aristas entre
ellos, y `workflows` (cadenas de llamada completas desde los puntos de entrada
hasta las hojas).

**Fichero 3 — Detalle de objeto concreto:**
`out/graph_full.nodes/objects/<id>/object.json` y `nav.json`
Los ids siguen el patrón `WideWorldImporters_<Schema>.<NombreObjeto>`, por ejemplo:
`out/graph_full.nodes/objects/WideWorldImporters_DataLoadSimulation.PopulateDataToCurrentDate/object.json`

**Empieza siempre por `audit_report.json` y `model.json`. No uses `graph_full.json`
ni ningún otro fichero fuera de `out/graph_full.nodes/`.**

---

## La petición de negocio — CR-2026-047

El equipo de Cumplimiento emite esta alerta:

> El proceso de simulación de carga de datos (`DataLoadSimulation`) desactiva la
> auditoría temporal (system-time) sobre tablas de negocio. Si falla a mitad, esas
> tablas quedan **sin auditoría indefinidamente** porque la reactivación no se
> ejecuta y no hay rollback.
>
> Necesitamos:
> 1. Mapa completo de todo lo que se ve afectado si tocamos este proceso
> 2. Flujo exacto de la lógica de negocio de principio a fin
> 3. Qué objetos son más peligrosos de cambiar y por qué
> 4. Un plan ordenado y seguro de refactorización por fases
> 5. Qué partes no se pueden analizar sin ejecutar el código

---

## Entregable

Escribe tu informe en `eval/agent-benchmark/results/track-B-result.md`
(sobreescribe el anterior). Usa este formato:

```markdown
# Informe CR-2026-047 — DataLoadSimulation [Track B — Nodestore]

## 1. Blast radius — Todo lo afectado
[tabla: objeto | tipo | cómo se ve afectado (directo/transitivo/vía datos)]

## 2. Flujo de negocio — Cadena de llamadas
[árbol indentado de la cadena completa con CC y score por nodo]

## 3. Evaluación de riesgo por objeto
[tabla: objeto | CC | SQL dinámico | nivel de riesgo | justificación]

## 4. Plan de refactorización por fases
[fases ordenadas: qué cambiar, en qué orden, por qué ese orden, cómo validar]

## 5. Límites del análisis estático
[qué no se puede saber sin ejecutar el código y por qué]

## 6. Qué aportó el nodestore
[qué información obtuviste directamente sin recorrer el grafo completo]

## Métricas del agente
- Ficheros leídos:
- Llamadas a herramientas aproximadas:
- Estrategia usada:
```
