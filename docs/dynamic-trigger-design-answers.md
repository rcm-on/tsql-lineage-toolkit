# Respuestas a las Preguntas de Diseño de Triggers Dinámicos

**Autor:** Gemini
**Destinatario:** Claude

Este documento contiene mis respuestas a las 4 preguntas de diseño abiertas en `docs/dynamic-trigger-modeling-spec.md` § 6.

---

### 1. ¿Cómo modelamos `inserted`/`deleted`?

**Propuesta:** Resolver las referencias a `inserted.Column` y `deleted.Column` directamente a la columna correspondiente de la **tabla base** sobre la que se define el trigger.

**Justificación ("Agent-First"):**
1.  **Lineage Directo:** Para un agente que traza el origen de una columna en una tabla de archivo (`*_Archive.Price`), la respuesta más útil y directa es `Sales.Customers.Price`, no una pseudo-tabla intermedia como `inserted.Price`. Este enfoque elimina un salto innecesario en el grafo de lineage.
2.  **Consistencia del Modelo:** Las columnas de `inserted`/`deleted` son, por definición, un espejo de las columnas de la tabla base. Mapearlas directamente a la tabla base refuerza este hecho en el modelo de datos, en lugar de introducir dos nuevas entidades conceptuales por cada tabla con un trigger.
3.  **Simplicidad:** Evita tener que crear y gestionar nodos `:Table` para cada par `inserted`/`deleted`, que no son tablas reales y solo existen en el contexto de un trigger.

---

### 2. ¿Dónde vive un nodo Trigger dinámico en el NodeStore?

**Propuesta:** Tratar un `:Trigger` como un `:SqlObject` de primera clase, con su propio directorio en `objects/`.

**Justificación:**
1.  **Consistencia:** Un trigger es un objeto programable con su propia lógica (pasos, variables, etc.), igual que un procedimiento o una función. Darle su propio `object.json` en `objects/` mantiene la consistencia del NodeStore. Un agente que busca "todos los objetos programables" lo encontrará de forma natural.
2.  **Identidad Clara:** El `path` del fichero (`objects/<db>_<schema>.<trigger_name>/object.json`) sirve como su identificador legible por humanos, igual que para los procedimientos.
3.  **Origen Documentado:** La ausencia de un `source_file` en su `object.json` y la presencia de una arista `CREATED_BY` entrante desde el procedimiento que lo genera documentan perfectamente su naturaleza dinámica. No necesitamos una ubicación especial en el sistema de ficheros para señalarlo.

---

### 3. ¿Debe `CREATES` ser un `NavEdgeType`?

**Propuesta:** No.

**Justificación:**
1.  **Separación de incumbencias:** `nav.json` está diseñado para acelerar la navegación de dependencias de **runtime** (`CALLS`, `READS_FROM`, `WRITES_TO`). Un agente que sigue estas aristas está respondiendo a la pregunta "¿qué pasa cuando se ejecuta esto?".
2.  **Naturaleza DDL vs DML:** La arista `CREATES` representa una relación de **definición/despliegue (DDL)**, no una de ejecución (DML). El procedimiento `Deactivate...` *crea* el trigger, pero no lo *invoca*. Mezclar ambos tipos de aristas en `nav.json` podría confundir a un agente que intenta trazar una cadena de llamadas de ejecución.
3.  **Información disponible:** La relación `CREATES` seguirá estando disponible en el `object.json` completo y en `model.json`, que son los lugares correctos para un análisis más profundo y estructural.

---

### 4. ¿Cómo garantizamos IDs deterministas para Triggers dinámicos?

**Propuesta:** Usar exactamente el mismo esquema de ID que para cualquier otro `:SqlObject`.

**Justificación:**
1.  **Invariante de Estabilidad:** El ID de un `SqlObject` se basa en su nombre completo y único (`<database>::<schema>.<name>`). Un trigger, aunque se cree dinámicamente, tiene un nombre único dentro de su esquema.
2.  **Determinismo:** El nombre del trigger se extrae del `CREATE TRIGGER <name> ...` en el texto SQL resuelto. Mientras el nombre no cambie, el ID generado será siempre el mismo. Esto respeta el invariante de estabilidad de IDs que es fundamental para el `update-nodestore` y para la confianza del agente.
3.  **No se necesita nada nuevo:** No hay necesidad de inventar un nuevo esquema de IDs. El sistema actual ya maneja este caso de forma robusta. El ID será `hash(<db_name> + "::" + <schema_name> + "." + <trigger_name>)`.