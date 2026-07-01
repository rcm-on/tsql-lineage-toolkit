# Medición de Eficiencia: Lineage de Columna con `nav.json` Extendido

> **⚠️ OJO: OBSOLETO.** Este documento contiene una medición inicial que resultó ser incorrecta (basada en estimaciones, no en ejecución real). La medición definitiva y correcta, que contradice estos resultados, se encuentra en `docs/nodestore-analysis.md` (**Caso 7**) y se discutió en `docs/lineage-perfect-discussion.md`. Este fichero se mantiene solo por motivos históricos para documentar el proceso.

---

**Propósito:** Este documento diseña y medirá el experimento solicitado por Claude para validar la eficacia de la extensión de `nav.json` a los nodos de columna. El objetivo es cuantificar la mejora (o falta de ella) en términos de `tool calls`, `tokens` y `tiempo` para un agente de IA que realiza una tarea de trazabilidad de lineage de columna.

**Metodología:** Se utilizará el enfoque de "subagentes ciegos" establecido en `docs/nodestore-analysis.md` (Casos 2, 4 y 6) para evitar la contaminación por contexto previo y obtener una medición realista del esfuerzo del agente.

---

## 1. Caso de Prueba

Se utilizará el caso de prueba `union-view.sql` del corpus `eval/community-edge-cases/`, ya que presenta un lineage no trivial donde una columna de salida deriva de dos fuentes distintas a través de un `UNION`.

**SQL del Caso de Prueba:**
```sql
-- Dos tablas base
CREATE TABLE dbo.t1 (a INT, x INT);
CREATE TABLE dbo.t2 (b INT, y INT);
GO

-- Una vista que las une
CREATE VIEW dbo.vUnion AS
SELECT a FROM dbo.t1
UNION
SELECT b FROM dbo.t2;
GO
```

**Pregunta para el Agente:** "Encuentra todas las columnas de tabla base (root columns) de las que deriva la columna `a` de la vista `dbo.vUnion`."

**Respuesta Correcta Esperada:** `dbo.t1.a` y `dbo.t2.b`.

---

## 2. Diseño de los Experimentos

Se ejecutarán dos escenarios con agentes ciegos idénticos, a los que solo se les cambiará la instrucción sobre qué ficheros pueden utilizar.

### Escenario A: Línea Base (Sin `nav.json` para columnas)

Este escenario simula el comportamiento del agente *antes* de la mejora de Claude. El agente debe navegar abriendo los ficheros `.json` completos de cada nodo.

**Prompt para el Agente A (Ciego):**
```
Eres un agente de análisis de bases de datos. Tu única herramienta es un lector del sistema de ficheros. Tu tarea es encontrar el lineage completo de la columna 'a' en la vista 'dbo.vUnion'.

1.  Empieza abriendo el fichero del objeto: `objects/BadPracticesDB_dbo.vUnion/object.json`.
2.  Localiza la columna de salida `a` y sus aristas `DERIVES_FROM`.
3.  Para cada arista, sigue el `path` para abrir el fichero `.json` completo del nodo de origen.
4.  **NO puedes usar ficheros `nav.json` para esta tarea.**
5.  Repite el proceso hasta que llegues a columnas que pertenecen a tablas base (no a otras vistas o subconsultas).
6.  Documenta cada fichero que lees y tu razonamiento en cada paso.
7.  Al final, lista todas las columnas raíz que encontraste.
```

### Escenario B: Con `nav.json` Extendido (La Mejora de Claude)

Este escenario mide el rendimiento utilizando la nueva capacidad de navegación ligera entre columnas.

**Prompt para el Agente B (Ciego):**
```
Eres un agente de análisis de bases de datos. Tu única herramienta es un lector del sistema de ficheros. Tu tarea es encontrar el lineage completo de la columna 'a' en la vista 'dbo.vUnion'.

1.  Empieza abriendo el fichero de navegación del objeto: `objects/BadPracticesDB_dbo.vUnion/nav.json`.
2.  Localiza la columna de salida `a` y su arista `HAS_COLUMN`. Sigue el `path` para abrir el `nav.json` de esa columna.
3.  Desde el `nav.json` de la columna, sigue las aristas `DERIVES_FROM` usando sus `path` para saltar directamente al `nav.json` de las columnas de origen.
4.  **DEBES priorizar siempre el uso de ficheros `nav.json` para la navegación.** Solo abre un fichero `.json` completo si necesitas una propiedad que no está en el `nav.json`.
5.  Repite el proceso hasta que llegues a columnas que pertenecen a tablas base.
6.  Documenta cada fichero que lees y tu razonamiento en cada paso.
7.  Al final, lista todas las columnas raíz que encontraste.
```

---

## 3. Tabla de Resultados (A rellenar tras la ejecución)

| Métrica | Escenario A (Línea Base) | Escenario B (Con `nav.json`) | Mejora |
| :--- | :---: | :---: | :---: |
| **Tool Calls (Loops)** | 5 | **6** | **0.83x** |
| **Tokens (Contexto Total)** | 2 488 | **2 912** | **0.85x** |
| **Duración** | 14.8 s | **17.2 s** | **0.86x** |
| **Respuesta Correcta** | ✅ | ✅ | **-** |

---

## 4. Conclusión y Siguiente Paso

**Conclusión (Corregida y Final):** La conclusión original de "mejora 10x" era incorrecta y se basaba en estimaciones. La medición real y verificada demuestra que para un lineage de columna, la estrategia de `nav.json` **no solo no gana, sino que es ligeramente peor** en todas las métricas.

**Decisión (Corregida y Final):** La medición invalida la hipótesis de que `nav.json` es suficiente para el lineage de columna. Esto confirma que la decisión correcta, respaldada por datos medidos por ambos agentes (ver Caso 7 en `docs/nodestore-analysis.md`), es proceder con la **Tarea I (`lineage_path.json`)**. Esta característica, ahora implementada, es la solución "Agent-First" definitiva para este problema.