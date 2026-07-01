# Oráculo de lineage independiente: sqlglot

Cruza el lineage de columna de **nuestro motor** (ScriptDOM) contra **sqlglot** como
oráculo independiente, para el subconjunto de **consultas** (SELECT/CTE/UNION/window/CASE).
Es el tercer oráculo del proyecto, junto a:

- `eval/view-lineage/` — oráculo = **SQL Server** (`sys.dm_sql_referenced_entities`).
- `eval/community-edge-cases/` — casos límite con runner propio.
- **este** — oráculo = **sqlglot** (parser independiente, dialect-aware).

## Por qué sqlglot (y sus límites)

sqlglot es robusto y dialect-aware para **lineage de consultas** → buen oráculo de ese
subconjunto (muy superior a node-sql-parser, que solo parseaba 11/24 vistas reales).
**Pero es query-only**: NO maneja `MERGE`/`INSERT`/`UPDATE`/`DELETE`/`OUTPUT`,
procedimientos, triggers, SQL dinámico ni impacto entre objetos (lanza
`"sql must be SELECT"`). Ese dominio procedural es el de nuestro motor. Por eso sqlglot
**complementa, no reemplaza**.

## Ejecutar

Requiere el binario Release compilado y [`uv`](https://docs.astral.sh/uv/) en PATH.
`uv` ejecuta sqlglot en un entorno efímero — **no instala nada permanente**.

```bash
# desde tsql-lineage-toolkit/
node eval/sqlglot-oracle/compare.mjs
```

Cada `cases/*.sql` es un SELECT puro: el comparador lo envuelve como `CREATE VIEW` para
nuestro pipeline y lo pasa tal cual a sqlglot, y compara —por columna de salida— el
conjunto de **nombres de columna fuente**. Marca `FALTAN(vs sqlglot)` cuando sqlglot ve
una fuente que nosotros no (= hueco de completitud).

## Estado actual (medido)

```
case-expr   x   nuestro={a,b,c}                sqlglot={a,b,c}                 OK
distinct    c   nuestro={a}                    sqlglot={a}                     OK
join-alias  …   nuestro={customerid,fullname}  sqlglot={customerid,fullname}   OK
union       a   nuestro={a,b}                   sqlglot={a,b}                   OK
window      rt  nuestro={amount,customerid,orderdate}  sqlglot={…}             OK
-> Sin huecos vs sqlglot
```

Tras los fixes de `UNION`/`CASE`/window, **estamos a la par con sqlglot** en lineage de
consultas. Añade casos a `cases/` para ampliar la cobertura del cruce.

## Archivos

| Archivo | Qué es |
|---|---|
| `sqlglot_lineage.py` | Oráculo: SELECT -> JSON `{columna: [fuentes]}` (vía `uv run --with sqlglot`). |
| `compare.mjs` | Cruza nuestro motor vs sqlglot por columna. |
| `cases/*.sql` | SELECTs de prueba (lineage de consulta). |
