# Comparativa contra otros parsers — ScriptDom vs. tree-sitter vs. sqlglot vs. Graphify

Medido sobre los mismos ficheros de entrada que usa `docs/corpus-multibase.md`
(mismo `input.json` de cada corpus, sin tocar nada), para responder una
pregunta muy concreta: **¿de verdad hace falta la gramática oficial de
Microsoft, o un parser SQL genérico ya vale?**

Herramientas usadas, todas de terceros, ninguna escrita por este proyecto:

- **tree-sitter genérico**, paquete `tree-sitter-language-pack==0.13.0`
  (Python) → `tree-sitter-sql` de DerekStride v0.3.11, gramática SQL genérica
  (ANSI/PostgreSQL-orientada). Es la misma gramática que usa Graphify.
- **tree-sitter específico de T-SQL**, `Crary-Systems/tree-sitter-tsql`
  (el único grammar de tree-sitter que existe específicamente para T-SQL).
  No está en PyPI ni trae binario para Windows — se compiló desde el fuente
  en un contenedor Linux (`python:3.12-slim` + `gcc`) porque su propio binding
  de Python trae un bug de nombres (`tree_sitter_tsql` vs. el símbolo real
  `tree_sitter_TSQL`, ver más abajo). Antes de aceptar el resultado se verificó
  con casos mínimos que el parser realmente cargó bien (parsea `SELECT` sin
  `WHERE` sin fallar) para descartar que el fallo generalizado fuera un error
  de nuestra construcción y no del grammar.
- **sqlglot** `30.14.0` (Python), con `dialect="tsql"` explícito — la opción
  más favorable posible para el rival: le decimos que el dialecto es T-SQL.
- **Graphify** (`Graphify-Labs/graphify`, `extractors/sql.py`, clonado en
  `c:\temp\graphify-src`) — el extractor de lineage SQL de un grafo de código
  multi-lenguaje real, no un parser genérico de laboratorio. Usa
  `tree-sitter-sql` (el mismo genérico de arriba) por debajo.

Metodología: por cada objeto del corpus, se parsea su `Sql` tal cual está en
el `input.json` ya existente (el mismo que consume `TSqlParser`). Se cuentan
tres cosas:

1. **Error de parseo** — la librería lanza una excepción y no produce ningún
   árbol usable.
2. **Nodo opaco** (`ERROR` en tree-sitter, `Command` en sqlglot) — la librería
   no lanza excepción, pero no entiende la sentencia y la trata como texto sin
   estructura: para una herramienta de lineage, esto es tan ciego como un
   error, porque de ahí no se puede extraer ninguna relación.
3. Si el fallo concreto está **relacionado con SQL dinámico** o es sobre
   **T-SQL básico** (verificado leyendo la línea exacta de cada caso, no por
   heurística de fichero completo — la primera pasada de este análisis
   clasificaba mal por eso, corregido aquí).

## Tabla final — los 5 corpus, las 4 herramientas, con el detalle completo

`sqlglot`, desglosado en las cuatro categorías reales (verificadas línea a
línea, no por heurística de fichero completo): error duro básico / error duro
por SQL dinámico / `Command` opaco básico / `Command` opaco por SQL dinámico.
"Limpios" = ninguna de las cuatro.

| Corpus | Objetos | ScriptDom — errores | tree-sitter genérico — limpios (% ERROR) | tree-sitter T-SQL — limpios (% ERROR) | sqlglot — limpios | sqlglot — error básico | sqlglot — error dinámico | sqlglot — `Command` básico | sqlglot — `Command` dinámico |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| WideWorldImporters | 95 | **0** | 1 (8,9%) | **0 (25,0%)** | 64 | 21 | 1 | 7 | 2 |
| AdventureWorks2019 | 123 | **0** | 0 (14,4%) | **0 (16,2%)** | 96 | 8 | 0 | 19 | 0 |
| Ola Hallengren | 8 | **0** | 0 (1,3%) | **0 (24,5%)** | 0 | 4 | 0 | 4 | 0 |
| First Responder Kit (curado) | 13 | **0** | 0 (3,6%) | **0 (47,7%)** | 1 | 12 | 0 | 2 | 0 |
| DarlingData | 14 | **0** | 0 (2,8%) | **0 (50,9%)** | 0 | 14 | 0 | 0 | 0 |
| **Total** | **253** | **0 (100%)** | **1 (0,4%)** | **0 (0%)** | **161 (63,6%)** | **59 (23,3%)** | **1 (0,4%)** | **32 (12,6%)** | **2 (0,8%)** |

**El dato incómodo: la gramática de tree-sitter dedicada específicamente a
T-SQL sale peor que la genérica** (0/253 limpios frente a 1/253, y peor
porcentaje de nodos `ERROR` en los 5 corpus). No es que aplicáramos mal
tree-sitter — se probaron las dos gramáticas que existen para SQL Server: la
genérica (razonable en DDL, ciega en lo procedural) y la única específica de
T-SQL, que resultó ser un proyecto de 5 estrellas, con bugs de léxico ya
documentados en su propio README, y que revienta en una condición tan básica
como `WHERE Id = 1` (verificado con un caso mínimo: `SELECT Id FROM
dbo.Customers;` parsea limpio, la misma consulta con `WHERE Id = 1` añadido
produce un nodo `ERROR` justo ahí). Ver `docs/oracle-parsers-comparativa/`
para el detalle de cómo se verificó que el fallo era del grammar y no de
nuestra compilación (build sin *scanner* externo, flags iguales a las
oficiales, casos de control que sí parsean limpio).

## Graphify — qué implica en la práctica perder el AST

No es un parser de laboratorio: es una herramienta real de grafos de código
multi-lenguaje, y su extractor SQL (`extractors/sql.py`, 295 líneas,
tree-sitter genérico) es una muestra honesta de qué pasa cuando ese fallo de
parseo llega hasta el resultado final. Mismos ficheros de entrada, ejecutando
su función `extract_sql` real:

| Corpus | Objetos | Nodos | Aristas de lineage real (`reads_from`/`references`/`triggers`) | Objetos con nodo creado pero **0** aristas de lineage |
|---|---:|---:|---:|---:|
| WideWorldImporters | 95 | 207 | 67 | **62/95 (65%)** |
| DarlingData | 14 | 94 | 0 | **14/14 (100%)** |

Separado por tipo de objeto en WWI, el patrón es binario: **las 23 tablas/vistas simples que sí tienen arista son DDL declarativo puro** (`CREATE TABLE ... REFERENCES`, `CREATE VIEW ... SELECT FROM`) — SQL casi ANSI, que cualquier gramática entiende. **Los 62 sin ninguna arista son, sin excepción, los 44 procedimientos/funciones** (`CREATE PROCEDURE`, cursores, `IF`, SQL dinámico) — la parte procedural de T-SQL, exactamente donde vive el argumento de este proyecto. El propio código de Graphify lo documenta: cuando el parser cae en un nodo `ERROR`, el *fallback* solo rescata el nombre por regex y **deliberadamente no escanea el cuerpo en busca de `FROM`/`JOIN`** (para no inventar relaciones falsas) — una decisión de diseño razonable, cuyo coste es que el objeto queda en el grafo sin ninguna relación.

No es una crítica a Graphify — nunca se propuso ser un motor de lineage SQL, y su extractor de 295 líneas no pretende serlo. Es la demostración, con su propio código y sus propios ficheros de entrada, de por qué hace falta un extractor SQL dedicado en vez de reutilizar un grafo de código genérico.

Lectura de la última fila: de los 253 objetos, **solo 3** (1,2%) fallan por
algo relacionado con SQL dinámico (1 error duro + 2 con `Command` opaco). Los
otros **91 objetos con problema** (59 + 32, el 35,9% del corpus) fallan por
T-SQL completamente ordinario — `RETURN`, `THROW`, `SET TRANSACTION ISOLATION
LEVEL`, `RAISERROR`, la forma de un `CREATE TRIGGER`. El SQL dinámico, que es
el argumento diferencial de este proyecto, es la parte **más pequeña** del
problema para `sqlglot` — falla mucho antes, en lo elemental.

`sp_Blitz` solo (el objeto más pesado, 480 KB, dentro de First Responder Kit):
0 errores en ScriptDom, **2.567 nodos `ERROR`** de 57.026 en tree-sitter.

## tree-sitter — detalle

El primer `ERROR` de `Configuration_ApplyAuditing` (78 líneas, uno de los
ejemplos del README) es la cabecera misma del procedimiento:
`CREATE PROCEDURE [Application].Configuration_ApplyAuditing AS` — la gramática
espera `CREATE PROCEDURE nombre(parámetros) AS $$...$$` (forma
PostgreSQL-like) y no reconoce corchetes de esquema, ausencia de paréntesis,
ni cuerpo en `BEGIN...END`. Tampoco reconoce `DECLARE @variable tipo = valor`
(pierde el nombre de la variable entero) ni un `IF` de una sola línea sin
`BEGIN/END`. No son casos raros: son las tres construcciones más universales
de cualquier procedimiento T-SQL.

**Conclusión:** una gramática SQL genérica no sirve para T-SQL real, ni
siquiera al nivel de sintaxis más básico. No es un problema de SQL dinámico
"avanzado" — falla en lo elemental.

## sqlglot (`dialect="tsql"`) — resultado, con la clasificación correcta

| Corpus | Objetos | Limpios del todo | Error duro — **T-SQL básico** | Error duro — SQL dinámico | `Command` opaco — básico | `Command` opaco — dinámico |
|---|---:|---:|---:|---:|---:|---:|
| WideWorldImporters | 95 | 64 | 21 | **1** | 7 objetos | 2 objetos (38 nodos) |
| AdventureWorks2019 | 123 | 96 | 8 | 0 | 19 objetos | 0 |
| Ola Hallengren | 8 | 0 | 4 | 0 | 4 objetos | 0 |
| First Responder Kit (curado) | 13 | 1 | 12 | 0 | 2 objetos | 0 |
| DarlingData (nuevo) | 14 | 0 | 14 | 0 | 0 | 0 |
| **Total** | **253** | **161 (63,6%)** | **59** | **1** | **32 objetos** | **2 objetos** |

**El hallazgo que no esperábamos:** casi todos los fallos —duros u opacos— son
sobre **T-SQL completamente básico**, no sobre SQL dinámico. Verificado línea
a línea, no por heurística:

- `RETURN 0;` / `RETURN -1;` — revienta el parseo en 13 procedimientos de WWI
  (`Integration.Get*Updates`, `Website.*`). Un `RETURN` con un entero es de lo
  más elemental que existe en T-SQL.
- `THROW 51000, N'mensaje', 1;` — el mismo problema, en otros 2 objetos de WWI
  y en Ola Hallengren.
- `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` — "Unknown option
  ISOLATION": rompe **10 de los 13** ficheros de First Responder Kit y **11 de
  los 14** de DarlingData. Es la primera línea ejecutable de casi cualquier
  script de diagnóstico de SQL Server.
- `RAISERROR('...', 0, 0, @param) WITH NOWAIT;` — falla en `sp_BlitzAnalysis`.
- `IF @IsHadrEnabled = 1` / `IF (SELECT ...) <> '/' AND CHARINDEX(...)` — `IF`s
  con nada de particular, fallan en Ola Hallengren y First Responder Kit.
- Estructura de un `INSTEAD OF` / `AFTER` trigger (`CREATE TRIGGER ... ON ...
  INSTEAD OF INSERT AS BEGIN ... END`) — 8 triggers de AdventureWorks2019 caen
  como `Command` opaco solo por el `CREATE`/`BEGIN`/`END` del trigger, sin
  llegar a analizar su cuerpo.

El caso de SQL dinámico real que sí encontramos —`DeactivateTemporalTablesBeforeDataLoad`,
el ejemplo insignia del propio README— produce **34 nodos `Command` opacos**
en `sqlglot`, mayormente las líneas `SET @SQL = N'DROP TRIGGER IF EXISTS ' +
QUOTENAME(...)` que reconstruyen los triggers dinámicos: exactamente la parte
que el motor propio sí resuelve y de la que extrae lineage cierto.

## Conclusión honesta (nada de venta de humo)

`sqlglot` con dialecto `tsql` es notablemente mejor que una gramática SQL
genérica — pero incluso con el dialecto explícito, **falla en construcciones
de T-SQL de primer día** (`RETURN`, `THROW`, `SET TRANSACTION ISOLATION
LEVEL`), no solo en el SQL dinámico que es el argumento diferencial del
proyecto. `ScriptDom` (la gramática oficial de Microsoft, la que ya usa todo
el motor) parsea los 253 objetos de los 5 corpus con **0 errores** — la única
de las tres que entiende T-SQL de producción de punta a punta, desde lo más
elemental hasta el SQL dinámico reconstruido.

## Cómo reproducirlo

```bash
pip install tree-sitter tree-sitter-language-pack sqlglot
python docs/oracle-parsers-comparativa/ts_compare_corpus.py <input.json>
python docs/oracle-parsers-comparativa/sqlglot_classify.py <input.json>
```

(Los dos scripts de comparación quedan en `docs/oracle-parsers-comparativa/`
para que cualquiera pueda repetir la medición sobre otro corpus.)
