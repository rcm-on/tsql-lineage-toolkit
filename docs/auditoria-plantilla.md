---
title: Plantilla de informe de auditoría
description: Cómo se compone un informe de auditoría con el motor, qué puede afirmar cada categoría y qué no.
read_when: Antes de generar un informe de auditoría, o al añadir una categoría nueva.
related: [docs/PROYECTO.md, docs/GLOSARIO-GRAFO.md, docs/VERIFICACION.md]
stability: durable
updated: 2026-08-19
---

# Plantilla de informe de auditoría

## La regla que lo gobierna

**Si falta la evidencia, la sección no se escribe: se declara no evaluable.**

Una sección de rendimiento sin planes de ejecución es especulación con formato de informe.
Es la diferencia entre una auditoría y una opinión larga, y es lo único que hace que este
informe valga más que el de cualquier herramienta que enumera hallazgos.

Reparto de responsabilidades: **el motor afirma, el modelo redacta**. La parte determinista
es la evidencia; la severidad y el impacto de negocio los pone una persona.

## 1. El objetivo decide las categorías

Un informe sin objetivo declarado enumera todo y no sirve para nada. El objetivo selecciona
qué categorías se abren y con qué umbrales:

| Objetivo | Categorías que se abren |
|---|---|
| Refactorización de un módulo | Calidad + impacto del cambio |
| Migración (versión o nube) | Calidad + superficie de inyección + rendimiento (exige planes) |
| Revisión previa a una entrega | Impacto del cambio + superficie de inyección |
| Toma de contacto con una base desconocida | Calidad + inventario + puntos ciegos |

## 2. Qué puede sostener cada categoría

### Calidad y mantenibilidad — autoridad real

Todo sale del store: `cyclomatic_complexity`, `total_steps`, `max_nesting`,
`has_error_handling`, `has_cursor`, `has_transaction`, fan-in/fan-out por `CALLS`, objetos
sin llamadores, y referencias por expansión de `SELECT *` (`resolution = star_expanded`).

### Superficie de inyección — parcial, y hay que nombrarlo bien

Puede afirmar: qué objetos construyen SQL dinámico a partir de variables, de qué variables,
y cuáles **no resolvieron** a texto literal.

**No** puede afirmar nada sobre permisos, `GRANT`, cifrado, datos personales ni
autenticación: no están en el grafo. Llamar a esto "auditoría de seguridad" sería falso.
Se llama auditoría de **superficie de inyección** y se dice por qué.

### Rendimiento — solo con planes de ejecución

Sin planes, el análisis estático da indicios (cursores, anidamiento, fan-out, `SELECT *`),
no coste. Con `capture-plans` y `enrich-from-plans` el grafo incorpora datos reales de
ejecución y entonces la sección se sostiene.

**Sin planes, la sección se declara no evaluable.** No se rellena con indicios disfrazados
de conclusiones.

## 3. Estructura del informe

### Portada — alcance y procedencia

De `store_info`: qué base, qué fecha de generación, cuántos objetos y aristas. Un informe
sobre un grafo de hace tres meses describe un pasado, y hay que decirlo en la primera
página, no en un anexo.

### Lo que NO se ha podido examinar

De `blind_spots`, y va **antes** que los hallazgos, no al final.

Objetos cuyo SQL dinámico no resolvió: podrían leer o escribir cualquier tabla y no hay
arista que lo pruebe ni lo descarte. Más las categorías sin base de evidencia en esta
ejecución (típicamente rendimiento, por falta de planes).

Casi ningún informe de auditoría declara su propio perímetro de ignorancia. Es lo que más
credibilidad da, y lo que más barato sale.

### Hallazgos por categoría

Cada hallazgo, sin excepción:

- **objeto y línea** — un hallazgo infalsificable no es un hallazgo;
- **severidad con su rúbrica escrita** — una letra sin criterio documentado es opinión
  disfrazada de dato;
- **confianza**: `seguro` si la referencia es literal en el SQL, `probable` si se dedujo
  (vía vista o expansión de `SELECT *`), con el motivo.

### Plan de tareas — ordenado por dependencia, no por severidad

Esta es la sección que distingue al informe. Casi todas las herramientas ordenan por
severidad, que es un juicio. Aquí el orden sale de la estructura real del grafo.

Pero **no es un orden, son dos**, y confundirlos produce planes que no se pueden ejecutar:

| Orden | Aristas | Regla |
|---|---|---|
| **Derivación** (datos) | `DERIVES_FROM` | La fuente antes que la derivada: tocar una columna calculada antes que su origen es retrabajo garantizado |
| **Ejecución** (control) | `CALLS`, `READS_FROM`, `WRITES_TO` | Quién llama a quién y quién escribe lo que otro lee |

El plan definitivo es un **orden topológico sobre la unión de los dos conjuntos de
aristas**, no sobre uno solo. `column_provenance` da el primero (más profundo primero) e
`impact` en dirección `upstream` da el segundo.

#### Cuando los dos órdenes se contradicen

Pasa, y hay que **declararlo, no resolverlo en silencio**. El caso típico: el orden de
derivación pide cambiar la columna origen primero, y el de ejecución dice que el
procedimiento que la escribe corre antes que el que la lee, así que cambiarlo deja un
intervalo en el que el lector está roto.

Ahí no hay un orden correcto: hay que **partir la tarea en un estado intermedio
compatible** —añadir lo nuevo, migrar los lectores, retirar lo viejo— y el informe tiene
que decir en qué punto exacto hace falta y por qué. Un plan que finge que existe un orden
único es un plan que rompe producción a mitad.

#### Ciclos

El T-SQL real tiene dependencias mutuas, así que el orden topológico puede no existir. Si
hay ciclo, **se reporta el ciclo con sus miembros**; no se elige un orden arbitrario. Un
plan ordenado que oculta un ciclo es peor que ningún plan, porque nadie sospecha de él
hasta que falla.

Cada tarea lleva: qué se cambia, qué se rompe si se cambia (de `impact` / `column_impact`),
qué hay que arreglar antes, y **por cuál de los dos órdenes viene esa precedencia**.

### Anexo — reproducibilidad

Los comandos exactos que generan el grafo y el informe. Mismo store y mismas reglas dan el
mismo informe: es la ventaja del motor determinista frente a un modelo redactando de cero,
y sin el anexo esa ventaja no se puede comprobar.

## 4. Qué falta hoy

- **`risks` no está expuesto por MCP.** `RiskAnalyzer` trabaja sobre `GraphPayload` y el
  servidor solo tiene el store SQLite. Sin hallazgos no hay informe: es el bloqueante.
- **No hay herramienta de evidencia.** Los `Step` guardan `line_no`, `action`,
  `target_name` y `condition_path`, y ninguna herramienta los expone.
- **La severidad no tiene rúbrica escrita.**
