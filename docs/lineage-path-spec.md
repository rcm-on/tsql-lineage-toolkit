# Especificación Técnica: `lineage_path.json` (Fase 3.2)

**Autor:** Gemini
**Destinatario:** Claude

## 1. Objetivo

Este documento formaliza la implementación de la "Opción 2 Refinada" que acordamos para el lineage de columna. El objetivo es eliminar la necesidad de que un agente de IA realice costosos recorridos de grafo para responder a la pregunta "¿de dónde viene este dato?".

Al pre-calcular y materializar las rutas de lineage, transformamos una tarea de O(V+E) para el agente en una lectura de fichero O(1). Esto es la culminación de nuestro diseño "Agent-First".

## 2. Formato del Fichero

*   **Nombre:** `objects/{slug}/lineage_path.json`
*   **Generación:** Se creará un único fichero por cada objeto que tenga columnas de salida (Vistas, TVFs, Procedimientos con `OUTPUT`, etc.).
*   **Contenido:** Un objeto JSON donde cada clave es el nombre de una columna de salida del objeto. El valor asociado a cada clave es un objeto que describe su lineage.

**Ejemplo para `dbo.v_QuarterlyFinancialReport`:**

```json
// en objects/dbo.v_QuarterlyFinancialReport/lineage_path.json
{
  "FinalRevenue": {
    "roots": [
      "dbo.RawTransactions.Price",
      "dbo.RawTransactions.Quantity"
    ],
    "immediate": [
      "dbo.v_StagingRevenue.AdjustedTotal"
    ],
    "depth": 3,
    "transformation_summary": null
  },
  "Quarter": {
    "roots": [
      "dbo.RawTransactions.TransactionDate"
    ],
    "immediate": [
      "dbo.v_StagingRevenue.TransactionDate"
    ],
    "depth": 2,
    "transformation_summary": null
  }
}
```

*   `roots`: Lista de todas las columnas de tabla base de las que deriva el dato. Esta es la respuesta a la pregunta de auditoría.
*   `immediate`: Lista de las columnas fuente directas en el paso de transformación anterior.
*   `depth`: La longitud del camino más largo desde la columna de salida hasta una de sus raíces.
*   `transformation_summary`: (Stretch goal, implementar como `null` por ahora). En el futuro, podría contener un resumen de la transformación (ej. "AGG(SUM)").

## 3. Algoritmo de Generación

La implementación debe residir en `NodeStoreExporter.cs` y ejecutarse como un post-proceso después de que el grafo principal esté construido.

1.  **Punto de Entrada (en `NodeStoreExporter.Write`)**: Dentro del bucle que itera sobre los `SqlObject`, después de escribir `object.json` y `nav.json`, llamar a una nueva función `WriteColumnLineagePaths(sqlObjectNode, objectDirectory)`.

2.  **Función Principal `WriteColumnLineagePaths`**:
    *   Identifica las columnas de salida del `sqlObjectNode` (siguiendo las aristas `HAS_COLUMN`).
    *   Si no hay columnas de salida, la función termina.
    *   Inicializa un `Dictionary<string, LineageResult> memoizationCache` que se pasará a través de todas las llamadas recursivas para evitar trabajo duplicado.
    *   Crea un diccionario para almacenar los resultados de lineage por nombre de columna.
    *   Para cada columna de salida, llama a la función de traversal `TraceLineage(outputColumnNode, memoizationCache, new HashSet<string>())`.
    *   Ensambla los resultados en el formato JSON especificado y escribe el fichero `lineage_path.json`.

3.  **Función de Traversal `TraceLineage(currentNode, cache, recursionStack)`**:
    *   **Memoización/Cache:** Si `currentNode.Id` está en `cache`, devuelve el resultado cacheado inmediatamente.
    *   **Detección de Ciclos:** Si `currentNode.Id` está en `recursionStack`, has encontrado un ciclo. Devuelve un resultado vacío para detener la recursión por esa rama.
    *   **Caso Base (Raíz):** Si `currentNode` es una columna de una tabla base (no tiene aristas `DERIVES_FROM` entrantes válidas), es una raíz. Devuelve un `LineageResult` que se contiene a sí mismo como raíz, con profundidad 0.
    *   **Paso Recursivo:**
        1.  Añade `currentNode.Id` a `recursionStack`.
        2.  Obtén los precursores inmediatos siguiendo las aristas `DERIVES_FROM` entrantes.
        3.  Inicializa una lista de `roots` y `immediate_sources` vacía, y `maxDepth = -1`.
        4.  Para cada precursor, llama recursivamente a `TraceLineage(precursorNode, cache, recursionStack)`.
        5.  Agrega los `roots` del resultado recursivo a tu lista de `roots` (eliminando duplicados).
        6.  Actualiza `maxDepth` con el `max(maxDepth, result.MaxDepth)`.
        7.  Añade el nombre del precursor a `immediate_sources`.
        8.  Quita `currentNode.Id` de `recursionStack`.
        9.  Crea el `LineageResult` final (con `depth = maxDepth + 1`), guárdalo en la `cache` y devuélvelo.

## 4. Consideraciones

*   **Rendimiento:** La memoización es clave. El lineage de una misma columna no debe ser recalculado nunca dentro de una misma ejecución del exportador.
*   **Robustez:** El manejo de ciclos es fundamental para evitar `StackOverflowException`.
*   **Completitud:** El algoritmo debe manejar correctamente los `UNION`, donde una columna puede tener múltiples precursores inmediatos que deben ser explorados.

---