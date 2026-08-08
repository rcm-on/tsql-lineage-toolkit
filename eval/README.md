# Corpus de evaluación

Los corpus contra los que se mide el motor se declaran en **[`corpora.json`](corpora.json)**.
Es la única lista: los gates leen de ahí sus suelos y el comando `corpus` regenera
desde ahí los ficheros congelados.

## Por qué un manifiesto

Hasta el 2026-08-08, el gate de recall de columna abría literalmente
`dnn-corpus.json` y llevaba sus cinco suelos como constantes dentro de la clase de
test. Añadir una segunda base obligaba a duplicar la clase entera — y con ella la
lógica de medición, que es justo la parte que no se puede duplicar sin que las dos
copias diverjan en silencio y dejen de ser comparables entre sí.

Con el manifiesto, **añadir un corpus es añadir una entrada de datos**. La medición
sigue siendo una sola, y los `[Theory]` de
[`ColumnRecallGateTests`](../tests/TSqlParser.Tests/ColumnRecallGateTests.cs) se
ejecutan una vez por corpus gateado.

## Campos

| Campo | Qué es |
|---|---|
| `id` | Identificador corto; es lo que se pasa a `corpus refresh <id>` y lo que sale en el nombre del caso de test. |
| `kind` | `schema-real` o `parser-torture`. Ver abajo — no es decorativo. |
| `license` / `provenance` | De dónde sale el corpus y bajo qué licencia se congela en el repo. Solo se congela lo permisivo. |
| `input` | El corpus en el formato de entrada del pipeline (`[{ "Name": ..., "Sql": ... }]`), con el DDL de las tablas incluido. |
| `oracle` | Ground-truth `módulo\|entidad\|columna`, en minúsculas. |
| `oracle_query` | El SQL que lo genera contra la base viva. |
| `source_db` | Base, servidor y `compatibility_level` con el que se extrajo. |
| `expected` | Invariantes de **forma** (`oracle_rows` exacto, `min_column_edges`). |
| `floors` | Suelos **medidos** de recall y de precisión por clase de evidencia. |

### `kind` es un guardarraíl, no una etiqueta

Un corpus **`parser-torture`** (Ola Hallengren, First Responder Kit) lee DMVs `sys.*`
y escribe en tablas temporales: apenas toca tablas de usuario y **no tiene columnas
catalogadas** contra las que medir. Ponerle un suelo de recall produciría un número
que no significa nada. Solo se gatean los **`schema-real`**.

`EvalCorporaManifestTests` obliga a que esa decisión sea explícita: un corpus
`schema-real` sin oráculo, sin suelos o sin expectativas hace fallar la suite, en vez
de quedarse fuera de los gates sin que nadie se entere.

### `expected` y `floors` no son lo mismo, y por eso se actualizan distinto

- **`expected`** son invariantes de forma, **derivadas** del fichero. `oracle_rows` se
  comprueba con **igualdad exacta**: si el fichero congelado cambia de tamaño, o se
  regeneró contra otra base o se truncó, y en ambos casos los suelos dejan de
  referirse a lo que se está midiendo.
- **`floors`** son cifras **medidas** corriendo el gate. Van **truncadas** por debajo
  del valor real, no redondeadas: el informe imprime un decimal y poner el suelo en el
  valor redondeado hace fallar al propio commit que lo mide (ya pasó dos veces).

## El comando `corpus`

```bash
TSqlParser corpus list                  # qué hay declarado, qué está gateado, con qué suelos
TSqlParser corpus refresh <id>          # regenera contra la base viva y DIFFEA (no escribe nada)
TSqlParser corpus refresh <id> --write  # además, sobrescribe los ficheros congelados
```

`refresh` sin `--write` es la operación barata y repetible: detecta que la copia
congelada **se ha separado** de la base viva. Sale con **código 2** si hay deriva, así
que sirve tal cual como comprobación de CI. Escribir hay que pedirlo: una regeneración
mueve las cifras de los gates, y eso tiene que ser una decisión.

`--write` actualiza los ficheros del corpus y `expected.oracle_rows` (derivado
mecánicamente), y **NO toca `floors`**. Un suelo es una cifra medida; copiarla desde
una regeneración sería fijar como invariante lo que el motor haga ese día, que es
justo como un trinquete deja de serlo.

### Regla al actualizar un corpus

**El commit que actualiza un corpus no toca el motor.** Si el corpus y el motor se
mueven a la vez, la cifra nueva no atribuye nada: no se sabe cuál de los dos la movió.
Dos commits, siempre.

## Gates que NO usan este manifiesto

- [`view-lineage/`](view-lineage/) — cross-check de vistas contra `sys.columns` y
  `sys.dm_sql_referenced_entities`. **Necesita SQL Server vivo.**
- [`bad-practices/`](bad-practices/), [`auditor-challenge/`](auditor-challenge/),
  [`agent-bench/`](agent-bench/) — corpus sintéticos con su propio formato de
  expectativas.
