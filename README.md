# T-SQL Lineage Toolkit

[![CI](https://github.com/rcm-on/tsql-lineage-toolkit/actions/workflows/ci.yml/badge.svg)](https://github.com/rcm-on/tsql-lineage-toolkit/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE) [![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

**Motor determinista de lineage e impacto para Microsoft SQL Server (T-SQL).** Apúntalo a tus procedimientos —desde un SQL Server vivo o desde ficheros `.sql`— y construye un mapa completo y consultable de *qué lee qué, qué escribe dónde y qué se rompe si lo cambias*. Hasta la columna. A través del SQL dinámico. Sin base de datos en marcha a la hora de consultar.

> Construido para **SQL Server / T-SQL** con la gramática oficial `ScriptDom`. No es un parser SQL genérico: entiende código procedural T-SQL —cursores, `EXEC(@sql)` dinámico, `MERGE`, tablas temporales, anidación multinivel.

## Objetivo

Antes de renombrar una columna, borrar una tabla o refactorizar un procedimiento de 2.000 líneas, necesitas **una respuesta en la que confiar: ¿qué depende de esto?** Las vías habituales no la dan bien:

- **`sys.sql_expression_dependencies`** es notoriamente incompleto: se pierde el SQL dinámico, la resolución diferida de nombres y las referencias cross-database.
- **Grep / búsqueda de texto** no distingue un `IF` real de uno dentro de un string `@sql` que se está construyendo, ni sigue una columna a través de un `INSERT ... SELECT`.
- **Un LLM leyendo el código** es no determinista: pregunta dos veces, dos respuestas — descalificado para una migración que tienes que firmar.

Este toolkit da una respuesta **determinista y consciente de la gramática**, como un artefacto portable que puedes diffear, versionar y usar como gate en CI.

## La pantalla de impacto

![Pantalla de impacto del dashboard sobre WideWorldImporters](docs/readme-impact.png)

### Lo que ves aquí y no verás con otra herramienta

Esta única pantalla —el procedimiento `DeactivateTemporalTablesBeforeDataLoad` de WideWorldImporters— muestra lo que las herramientas de texto y las de catálogo dejan escapar:

- **34 sentencias de SQL dinámico, resueltas y contadas.** El procedimiento construye su SQL en `@SQL` en tiempo de ejecución y lo lanza con 34 `EXECUTE (@SQL)` — el AST cuenta **34**, y el fuente tiene exactamente 34. El contraste está en el flujo de control: un `grep` del cuerpo encuentra **52** tokens `IF`, pero **34 de ellos viven *dentro* de los strings que se están construyendo**; el AST reconoce los **18** reales y una anidación de **1**. Ese hueco, justo donde el análisis de texto falla, es el sentido de todo.
- **Reglas de negocio y riesgos, no solo dependencias.** El objeto **escribe en 17 tablas distintas** ("hace demasiado, candidato a dividir"), **modifica datos sin transacción ni manejo de errores**, y ejecuta SQL dinámico ("revisar parametrización/permisos"). Riesgos de seguridad, robustez y mantenibilidad derivados del AST, con severidad.
- **Flujos de control reales:** complejidad ciclomática 19, 18 flujos de control, 87 pasos — métricas del árbol de sintaxis, no del texto.
- **El grafo de impacto:** a quién llama, quién le llama, y las tablas que toca con su operación (`ALTER`, `lee`, …). Incluso detecta que **crea triggers dinámicamente**.
- **Resumen en lenguaje natural**, automático, arriba del todo — para que un humano (o un LLM) entienda el objeto sin leer 87 pasos.

Todo esto es offline, sin servidor, arrastrando un fichero al [dashboard](dashboard/).

## El impacto, por niveles y profundidad

![Cadena de impacto por niveles: un procedimiento, los que llama, y las tablas y vistas afectadas](docs/readme-impact-chain.png)

La cadena de impacto se despliega **por niveles**, aguas arriba y aguas abajo, hasta la profundidad que elijas (1–5). Aquí `Configuration_ConfigureForEnterpriseEdition` → **Nivel +1**: los 4 procedimientos que ejecuta → **Nivel +2**: las tablas donde acaban insertando → **Nivel +3**: la vista que las lee. De un vistazo tienes el **radio de impacto completo**, no una lista plana: sabes a cuántos saltos está cada cosa de lo que vas a tocar.

## El flujo de negocio, paso a paso

![Flujograma de control con decisiones IF reconstruido desde el AST](docs/readme-flow.png)

Cada procedimiento se traduce a su **flujograma real** desde el AST — con sus **decisiones**, no un resumen. Aquí `Configuration_ApplyAuditing`: *¿existe ya `WWI_ServerAuditSpecification`?* → si no, lo crea con SQL dinámico (`EXEC ⚡`); *¿el servidor soporta especificaciones de auditoría?* → ramifica. Cada `IF` con sus ramas **sí/no** en lenguaje natural y la línea exacta. La lógica de negocio, con sus condiciones, legible sin abrir el `CREATE PROCEDURE`.

## Comparación — qué hueco llena

**vs. herramientas open source / gratuitas:**

| Herramienta | AST real | SQL dinámico | Lineage columna | Riesgos | Offline / agent-ready |
| --- | :---: | :---: | :---: | :---: | :---: |
| **Este toolkit** | ScriptDom | ✅ | ✅ | ✅ | ✅ (NodeStore) |
| `sys.sql_expression_dependencies` | ❌ catálogo | ❌ | ❌ | ❌ | — |
| sqllineage (Python) | ❌ regex | ❌ | ❌ | ❌ | ❌ |
| SQLGlot | ✅ multi-dialecto | ❌ | parcial | ❌ | ❌ |
| Apache Atlas | catálogo | ❌ | ❌ | ❌ | genérico |
| dbt lineage | solo modelos dbt | ❌ | limitado | ❌ | ❌ |

**vs. herramientas comerciales:**

| Herramienta | Precio | AST T-SQL | SQL dinámico | Offline | Open source |
| --- | :---: | :---: | :---: | :---: | :---: |
| **Este toolkit** | **Gratis** | ✅ | ✅ | ✅ | ✅ |
| Microsoft Purview | €€€ · Azure-only | ❌ catálogo | ❌ | ❌ | ❌ |
| Octopai | €€€€ enterprise | ❌ | ❌ | ❌ | ❌ |
| Informatica IDMC | €€€€€ enterprise | ❌ | ❌ | ❌ | ❌ |

### El hueco que complementa

Las gratuitas paran en el análisis de texto o de catálogo: **ninguna usa la gramática `ScriptDom` ni resuelve el `EXEC(@sql)` dinámico**, que es exactamente donde vive el riesgo. Las de pago sí razonan sobre metadatos, pero cuestan una fortuna, viven en cloud y **tampoco entran en el SQL dinámico ni dan lineage a columna con reglas de negocio**. Este toolkit ocupa el hueco intermedio: la profundidad de gramática de una comercial, gratis, offline y diffable — pensado para **complementar** tu SSMS, tu catálogo y tu CI, no para sustituirlos.

### Validado contra SQL real (y contra oráculos)

No es solo WideWorldImporters. La corrección se contrasta contra **varios corpus con oráculo independiente**:

- **Código fuente real (WWI):** `DeactivateTemporalTablesBeforeDataLoad` reporta **34** sentencias de SQL dinámico → el fuente tiene exactamente **34** `EXECUTE (@SQL)`. Y **18** flujos de control → los que quedan al descontar los **34** tokens `IF` que un grep encuentra *dentro* de los strings generados (52 en crudo).
- **Malas prácticas (`eval/bad-practices/`):** un corpus de anti-patrones con `expected-findings.json` como oráculo — detección de SQL dinámico, escrituras sin transacción, complejidad, variables muertas.
- **Construcciones complejas (`eval/community-edge-cases/`):** `MERGE`, CTEs recursivas, SQL dinámico, cursores — los casos que rompen a los parsers de texto.
- **Lineage de columna (`eval/view-lineage/`):** contrastado contra **`sys.dm_sql_referenced_entities`** del propio SQL Server.

Además de los corpus, **136 pruebas unitarias (xUnit)** cubren el parser. Todo corre como **gate en CI**: una regresión sale en rojo antes que en manos de un usuario.

## Casos de uso — dónde aparece este problema

No sustituye a tu SSMS ni a tu CI: los **complementa** con la respuesta que ninguno te da rápido.

- **Antes de renombrar o borrar una columna/tabla** — el radio de impacto completo (procs, vistas, columnas derivadas) en segundos, no leyendo 40 procedimientos a mano.
- **Refactorizar un procedimiento heredado** — la **cadena de llamadas** entrante y saliente: quién lo llama, a qué llama, qué tablas toca y con qué operación.
- **Migración / modernización** — inventario de dependencias real antes de mover esquemas o plataformas; incluye lo que vive en SQL dinámico y cursores.
- **Auditoría de seguridad** — dónde se construye SQL dinámico (superficie de inyección), qué escribe sin transacción ni manejo de errores, operaciones destructivas.
- **Gate en el PR** — genera el grafo en cada cambio y diféalo: falla si aparece una escritura no documentada o se rompe un lineage, dentro de tu pipeline.
- **Onboarding / base de datos heredada** — un mapa navegable en vez de 500 procedimientos sin documentar.
- **Gobernanza y datos sensibles (PII)** — traza el origen de un dato hasta la columna que lo produce (provenance).
- **Deprecación** — ¿este objeto lo llama alguien todavía? Detecta lo que ya nadie usa.
- **Base de hechos para agentes IA** — un grafo consultable con SQL para que un LLM responda con certeza en vez de adivinar sobre el T-SQL crudo.

## Números reales

Ejecutado contra **WideWorldImporters** (base de datos de muestra de Microsoft), no un ejemplo de juguete:

| Métrica | Valor |
| --- | --- |
| Objetos extraídos de la base | 47 procedimientos/funciones/vistas + 48 tablas |
| Objetos en el grafo | **64** (los 47 + **17 triggers creados en runtime** por SQL dinámico) |
| Tablas en el grafo | **68** (las 48 + 15 del catálogo `sys.*` referenciado + 3 vistas + 2 tablas creadas en runtime) |
| Nodos del grafo | 1.529 |
| Relaciones | 4.151 |
| Errores de parseo | **0** |
| Claves ajenas contra `sys.foreign_keys` | **98 / 98** — 0 ausencias, 0 fantasmas |
| Cadenas `EXEC` contra `sys.sql_expression_dependencies` | **12 / 12** — 0 ausencias |
| Cobertura de lineage de columna | **32 / 32 columnas de salida (100%)** |

Las dos primeras filas son la razón de ser de la herramienta: la base **no tiene ningún trigger** en `sys.objects`, pero el análisis del AST descubre los **17** que `DeactivateTemporalTablesBeforeDataLoad` crea en tiempo de ejecución. Un inventario de catálogo se los pierde enteros.

> Un nodo `Table` no es siempre una tabla base: una **vista** también recibe uno, para que un `SELECT col FROM vista` aguas abajo aterrice en el mismo nodo `Column` y el lineage no se corte al atravesarla.

*(Corrida canónica del **2026-07-26** contra `.\SQLEXPRESS` · SQL Server 2025 (RTM-GDR) 17.0.1125.2 Express · commit `487e15c`. Salidas de consola literales, capturas y desglose completo en [`docs/corrida-canonica.md`](docs/corrida-canonica.md).)*

## Guía de uso

Necesitas **.NET 10** y, para el modo con base de datos viva, acceso a una instancia de SQL Server.

```bash
cd src/TSqlParser

# A) Desde un SQL Server vivo (incluye DDL de tablas → lineage de columna + FKs)
dotnet run -- extract MiBaseDatos ../../input.json --server .\SQLEXPRESS --tables

# B) O totalmente offline, desde ficheros .sql
dotnet run -- from-sql MiBaseDatos ../../input.json sql/*.sql

# Construir el grafo de lineage + la base SQLite consultable
dotnet run -- ../../input.json ../../graph_full.json --columns --sqlite --nodestore
```

Salidas: **`graph_full.json`** (grafo canónico, diffable — versiónalo), **`graph_full.db`** (SQLite consultable con SQL) y el **NodeStore** (`--nodestore`, representación optimizada para agentes IA).

### Consultar impacto con SQL

La base SQLite tiene `nodes` y `edges`. *"¿Qué se rompe si cambio una columna?"* — transitivo, por derivación de columnas **y** cadenas de llamada:

```sql
WITH RECURSIVE
  affected(col) AS (
    SELECT 'WideWorldImporters:table:sales.orderlines:column:UnitPrice'
    UNION SELECT e.src FROM edges e JOIN affected ON e.dst=affected.col
    WHERE e.type='DERIVES_FROM'),
  proc(p) AS (
    SELECT DISTINCT substr(e.src,1,instr(e.src,'#')-1)
    FROM edges e JOIN affected ON e.dst=affected.col
    WHERE e.type IN ('READS_COLUMN','WRITES_COLUMN'))
SELECT n.name FROM proc JOIN nodes n ON n.id=proc.p WHERE n.label='SqlObject';
```

### Ejemplos de auditoría (una consulta cada uno)

```sql
-- ¿Qué objetos acceden a una tabla concreta?
SELECT DISTINCT substr(src,1,instr(src,'#')-1) FROM edges
WHERE dst LIKE '%:table:warehouse.stockitems' AND type IN ('READS_FROM','WRITES_TO');

-- ¿Dónde se construye SQL dinámico, y cuánto? (superficie de inyección)
SELECT name, dynamic_sql_steps FROM nodes
WHERE label='SqlObject' AND dynamic_sql_steps>0 ORDER BY dynamic_sql_steps DESC;

-- Procedimientos que modifican datos SIN TRY/CATCH
SELECT name FROM nodes WHERE label='SqlObject' AND has_error_handling=0;

-- Operaciones destructivas (DELETE / TRUNCATE / DROP)
SELECT action, COUNT(*) FROM nodes
WHERE label='Step' AND action IN ('DELETE','TRUNCATE','DROP') GROUP BY action;
```

Consultas listas en `scripts/lineage-queries.sql` (`node scripts/run-query.js @audit_dynamic_sql`), o abre `graph_full.db` en DB Browser / DBeaver.

## Dashboard visual (offline, sin build)

Abre [`dashboard/index.html`](dashboard/) con doble clic, **arrastra tu `graph_full.json`** y explora al instante:

![Resumen general del dashboard](docs/readme-overview.png)

Resumen general, vista por objeto/tabla con flujo de control en **lenguaje natural**, cadena de impacto multinivel, panel de riesgos y esquema ORM interactivo — todo sin servidor.

### Auditoría de riesgos, de una pasada

![Panel de riesgos: hallazgos por severidad y categoría con el detalle de cada regla](docs/readme-risks.png)

El panel de riesgos clasifica cada hallazgo por **severidad** y **categoría**. Sobre WWI: **110 hallazgos** (1 crítico, 20 altos, 43 medios, 46 bajos), con el detalle de la regla — desde una **inyección SQL** (el único crítico: `Configuration_ApplyColumnstoreIndexing` construye `@SQL` desde datos de `sys.indexes`) hasta escrituras sin transacción, complejidad excesiva o problemas de integridad. La auditoría de seguridad y calidad que normalmente requiere una herramienta de pago, en un panel.

## En tu CI/CD — un gate de impacto en cada PR

El toolkit genera un `change_map` por rama y los **diffea**: `diff-change-map` sale con código **2** cuando el cambio introduce impacto nuevo, así que se convierte en un gate de PR que **falla si tocas algo con radio de impacto no revisado**.

```yaml
# .github/workflows/impacto.yml
name: Impacto SQL
on: pull_request
jobs:
  impact:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: "10.0.x" }

      - name: Grafo de la rama base
        run: |
          git worktree add ../base ${{ github.event.pull_request.base.sha }}
          dotnet run --project src/TSqlParser -- from-sql MiBase base.json ../base/sql/*.sql
          dotnet run --project src/TSqlParser -- base.json base_graph.json --nodestore

      - name: Grafo de la rama del PR
        run: |
          dotnet run --project src/TSqlParser -- from-sql MiBase head.json sql/*.sql
          dotnet run --project src/TSqlParser -- head.json head_graph.json --nodestore

      - name: Gate de impacto (falla si hay impacto nuevo)
        run: dotnet run --project src/TSqlParser --
             diff-change-map base_graph.nodes head_graph.nodes change_map_diff.json --fail-on-new-impact
```

El `change_map_diff.json` queda como artefacto: qué objetos cambiaron y a quién afectan. Ideal para **comentar el PR** con el radio de impacto exacto antes de aprobar.

## Limitaciones (honestas)

- **Solo SQL Server / T-SQL** (`ScriptDom`); otros dialectos quedan fuera.
- **Guarda el lineage analizado, no el fuente.** Pregunta "¿qué depende de X?", no "muéstrame el T-SQL de X".
- **El SQL dinámico se resuelve solo hasta donde es reconstruible estáticamente**; si `@sql` se arma con valores invisibles al análisis, el paso se marca como dinámico, no se adivina.
- **Sin scoring de confianza todavía**: una arista cierta y una inferida se ven igual (planificado).
- **Completitud alta, no total**: la ausencia de una arista es "no detectada", no "probado que no existe".

## Pruébalo contra tu base de datos

Está probado contra una base de datos **real** —**WideWorldImporters sobre SQL Server 2025 (17.0.1125.2, Express)**— y sus construcciones más difíciles: **SQL dinámico** (`EXEC(@sql)`), **cursores**, **`MERGE`**, **tablas temporales**, **triggers creados en runtime** y anidación multinivel. Sale con 0 errores de parseo, y el lineage se contrasta contra oráculos independientes (`sys.dm_sql_referenced_entities`, planes de ejecución). La completitud es alta, pero no infinita.

Por eso la invitación es directa: **apúntalo a tu base de datos y pruébalo.** Si encuentras un objeto que no extrae bien —una tabla que se pierde, un lineage que se corta, un patrón raro—, **ábrelo como issue con el caso**. Cada release estrecha el hueco, y los casos reales son el mejor corpus. Trata la ausencia de una arista como "no detectada", no como "probado que no existe" — y cuéntanoslo para mejorarlo.

## Licencia

MIT © Ramón Campos Martín — [blog.rcmon.dev](https://blog.rcmon.dev)
