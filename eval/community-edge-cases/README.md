# Casos límite de comunidad (corpus reproducible — Tarea F)

Casos T-SQL mínimos y aislados que sondean huecos de extracción de lineage, con un
**runner que EJECUTA el pipeline de verdad** (no narra). Acompaña a
[`docs/extraction-gaps.md`](../../docs/extraction-gaps.md), que documenta el análisis.

## Ejecutar

```bash
# desde tsql-lineage-toolkit/ (compila antes: dotnet build src/TSqlParser/TSqlParser.csproj -c Release)
node eval/community-edge-cases/run.mjs
```

El runner procesa cada `.sql` (`from-sql` → `graph --columns`), lee el JSON resultante y
reporta las aristas `DERIVES_FROM`/`READS_FROM` reales, comparando con el mínimo esperado.
Sale `!=0` si algún caso queda por debajo.

## Casos

| Caso | Estado | Esperado |
|---|---|---|
| `dml-advanced/merge.sql` | ✅ arreglado | `DERIVES_FROM` columna-a-columna en `UPDATE SET` + `INSERT VALUES` |
| `dml-advanced/merge-with-output.sql` | ✅ arreglado | `UPDATE/INSERT` + lineage por `OUTPUT INTO`: `log.col <- target.col` vía `inserted`/`deleted` |
| `cte-recursive/recursive-cte.sql` | ✅ arreglado | `READS_FROM` a la tabla base + `DERIVES_FROM`; `Lvl` (columna calculada) mapea a un fantasma (limitación conocida) |
| `window-functions/window.sql` | ✅ guarda de regresión | NO era gap; el lineage a través de `OVER(PARTITION BY/ORDER BY)` debe extraerse |
| `set-ops/union-view.sql` | ✅ arreglado | vista `UNION`: la columna de salida deriva de la columna posicional de CADA rama (`a <- t1.a`, `a <- t2.b`) |

## Resultado actual (medido)

```
OK merge.sql              DERIVES_FROM=3  READS_FROM=1
OK merge-with-output.sql  DERIVES_FROM=5  READS_FROM=2   (OUTPUT: log.col <- target.col)
OK recursive-cte.sql      DERIVES_FROM=3  READS_FROM=1   (Lvl <- fantasma)
OK window.sql             DERIVES_FROM=6  READS_FROM=1
OK union-view.sql         DERIVES_FROM=2  READS_FROM=2   (a <- t1.a, a <- t2.b)
```

> Nota de procedencia: corpus creado y ejecutado por Claude tras detectar que los ficheros
> no se habían materializado en disco. Gemini (Tarea F) aportó el análisis en
> `docs/extraction-gaps.md`.
