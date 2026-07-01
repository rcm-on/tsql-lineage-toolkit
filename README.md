# T-SQL Lineage Toolkit

Este proyecto es un motor de análisis de impacto y lineage para T-SQL, diseñado para la toma de decisiones asistida por LLM. Su objetivo principal es lograr la máxima completitud en la extracción de dependencias para proporcionar una visión macro y permitir la remediación ordenada de riesgos en bases de datos complejas.

## Pipeline de Análisis

El pipeline principal consta de dos pasos:

1.  **`from-sql`**: Parsea un conjunto de ficheros `.sql` y genera un `input.json` intermedio.
    ```bash
    dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll from-sql <DatabaseName> out/input.json sql/**/*.sql
    ```

2.  **`graph`**: Procesa el `input.json` para generar el grafo de dependencias completo (`graph_full.json`) y el `NodeStore` (`--nodestore`), que es una representación optimizada para la consulta por parte de agentes de IA.
    ```bash
    dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll out/input.json out/graph_full.json --columns --nodestore
    ```

## Estructura del Repositorio y Control de Versiones

Para mantener el repositorio limpio y enfocado en el código fuente, utilizamos un fichero `.gitignore` que excluye artefactos generados, dependencias y ficheros específicos del entorno.

### Ficheros Ignorados (`.gitignore`)

El fichero `.gitignore` está configurado para ignorar los directorios `bin/`, `obj/`, `node_modules/` y, muy importante, todos los directorios de salida como `out/` y `eval/*/out/`. Estos contienen los `NodeStores` y grafos generados, que pueden ser muy grandes y se pueden reproducir en cualquier momento ejecutando el pipeline.

### Ficheros `.sql` (Casos de Prueba)

Los ficheros con extensión `.sql` que se encuentran en los directorios `eval/` (por ejemplo, en `eval/community-edge-cases/` o `eval/bad-practices/sql/`) **son una parte fundamental del código fuente del proyecto**.

Estos ficheros no son scripts de despliegue, sino nuestro **corpus de validación y casos de prueba**. Nos permiten verificar que el analizador extrae el lineage correctamente, probar el motor contra construcciones T-SQL complejas y prevenir regresiones.

Por este motivo, los ficheros `.sql` de los casos de prueba **no se ignoran** y deben ser versionados en Git junto con el resto del código del analizador.

## Evaluación y Casos de Prueba

El directorio `eval/` contiene varios corpus de prueba para validar diferentes aspectos del analizador. Cada corpus tiene su propio script de ejecución y su `README.md` específico.

### `eval/bad-practices/`

Este corpus valida el motor de detección de malas prácticas. Contiene un conjunto de ficheros SQL con anti-patrones conocidos y un `expected-findings.json` que actúa como oráculo.

**Ejecución:**
```bash
cd eval/bad-practices
./run.sh # o ./run.ps1 en Windows
```

### `eval/community-edge-cases/`

Prueba la completitud del parser contra construcciones T-SQL complejas que han sido identificadas como gaps (`MERGE`, CTEs recursivas, SQL dinámico, etc.).

**Ejecución:**
```bash
node eval/community-edge-cases/run.mjs
```

### `eval/view-lineage/`

Valida la corrección del lineage de columnas de vistas contra el oráculo de SQL Server (`sys.dm_sql_referenced_entities`).

**Ejecución:**
```bash
node eval/view-lineage/crosscheck.mjs <ruta_al_nodestore>
```

### `eval/auditor-challenge/`

Automatiza la verificación de las métricas clave identificadas en los informes de auditoría, actuando como un test de regresión para los hotspots más importantes de WideWorldImporters.

**Ejecución:**
```bash
node eval/auditor-challenge/verify.mjs <ruta_al_nodestore_wwi>
```