# Ejercicio de Auditoría Cruzada: Gemini vs. Claude

**Propósito:** Este documento define un ejercicio de validación para ser ejecutado en paralelo y a ciegas por Gemini y Claude. El objetivo es auditar y comparar cómo dos agentes diferentes, utilizando el mismo `NodeStore` con diseño "Agent-First", abordan tareas de consultoría complejas.

---

## 1. Objetivo del Ejercicio

Ambos agentes (Gemini y Claude) actuarán como consultores/auditores expertos. Cada uno, de forma independiente, realizará las dos tareas descritas a continuación y generará un informe separado. Al final, compararemos los dos informes resultantes para evaluar la consistencia, el enfoque y la profundidad del análisis que permite nuestro `NodeStore` mejorado.

**Regla fundamental:** No se debe compartir ni discutir el contenido de los informes hasta que ambos agentes hayan completado y guardado sus resultados.

---

## 2. Tarea 1: Plan de Mejora Estratégico

Esta tarea evalúa la capacidad de sintetizar una visión macro y un plan de acción priorizado a partir de los datos agregados y de malas prácticas.

### Prompt para el Agente (Gemini y Claude)

> Asume el rol de un consultor de bases de datos experto. Se te ha dado acceso al `NodeStore` completo (que incluye `WideWorldImporters` y el corpus de `bad-practices`). Tu tarea es analizar este `NodeStore` y producir un **Plan de Mejora Estratégico**.
>
> Tu informe debe incluir:
> 1.  **Identificación de "Hotspots":** Lista las 3-5 tablas y procedimientos más críticos o problemáticos.
> 2.  **Priorización de Tareas:** Crea una lista ordenada de tareas de refactorización o corrección, desde la más urgente a la menos.
> 3.  **Justificación "Agent-First":** Para cada punto, justifica tu decisión citando métricas específicas del `NodeStore` (ej. `degree`, `cyclomatic_complexity`) y, crucialmente, utilizando el `lineage_path.json` para conectar los problemas técnicos con su impacto en el negocio (ej. "refactorizar X porque impacta en el informe Y").

---

## 3. Tarea 2: Análisis de Impacto de un Cambio

Esta tarea evalúa la capacidad de realizar un análisis de impacto preciso y detallado, combinando la navegación de dependencias funcionales con el lineage de datos.

### Prompt para el Agente (Gemini y Claude)

> Asume el rol de un arquitecto de software. El equipo de desarrollo planea modificar el procedimiento `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`.
>
> Tu tarea es realizar un **Análisis de Impacto Completo** utilizando el `NodeStore` proporcionado. Tu informe debe detallar:
> 1.  **Impacto Funcional (Upstream/Downstream):** Usando `nav.json`, identifica qué procedimientos llaman a este y a cuáles llama él.
> 2.  **Impacto en Datos (Efecto Dominó):**
>     a. Identifica las tablas en las que escribe directamente.
>     b. Para cada tabla escrita, utiliza `lineage_path.json` para determinar si este cambio podría afectar indirectamente a alguna vista o informe final importante.
> 3.  **Riesgos Clave:** Basado en la información del `NodeStore`, resume los 3 riesgos principales a considerar al realizar este cambio.

---

## 4. Protocolo de Ejecución y Entrega

1.  **Ejecución en Paralelo:** Claude y Gemini ejecutarán las Tareas 1 y 2 de forma independiente.
2.  **Generación de Informes:**
    *   **Claude:** Guardará sus dos informes (Plan de Mejora y Análisis de Impacto) en un único fichero nuevo: `docs/claude-audit-report.md`.
    *   **Gemini:** Guardará sus dos informes en un único fichero nuevo: `docs/gemini-audit-report.md`.
3.  **Notificación:** Una vez que un agente haya guardado su fichero, lo notificará en la bitácora de `agent-collab.md` sin revelar ningún contenido.
4.  **Comparación:** Solo cuando ambos agentes hayan notificado la finalización, procederemos a comparar los dos ficheros de resultados.

---

## 5. Ronda 2 (2026-06-30): re-test tras Tarea I + fix de `QUOTENAME`

Desde la Ronda 1 (informes guardados en `docs/claude-audit-report.md` y
`docs/gemini-audit-report.md`) cambiaron dos cosas reales en el NodeStore:

1. **Tarea I cerrada** después de la Ronda 1 — `lineage_path.json` ya estaba disponible
   cuando se hizo la Ronda 1, pero conviene re-confirmar que las cifras de cobertura
   (`Website.Customers`/`Suppliers`) siguen siendo las mismas.
2. **Fix de `QUOTENAME`** (`AstWalker.ResolveLiteral`, ver `docs/extraction-gaps.md` § 5.1):
   `unresolved_dynamic_sql_steps` de `DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad`
   bajó de 34 a 17. La Ronda 1 citaba 34/34 como parte del hotspot #3.

**Tarea (la misma Tarea 1, "Plan de Mejora Estratégico", repetida en ciego):** vuelve a
analizar el `NodeStore` actual (regenera `out/` si hace falta) y produce el mismo Plan de
Mejora Estratégico. **Esta vez, además, compara tu propio resultado contra tu propio informe
de la Ronda 1** (no contra el del otro agente — eso se hace después, como siempre) y reporta
explícitamente: qué cambió, qué se mantiene igual, y si el cambio detectado coincide con lo
que predice el motivo técnico (1) y (2) de arriba o si encontraste algo inesperado.

**Entrega:** añade tu Ronda 2 a tu propio fichero (`claude-audit-report.md` /
`gemini-audit-report.md`), no crees ficheros nuevos — una sección `## Ronda 2` al final.
Mismo protocolo de no compartir hasta que ambos notifiquen en `agent-collab.md`.

---