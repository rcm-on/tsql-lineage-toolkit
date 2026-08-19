"""Oráculo de lineage de columna independiente (sqlglot), para cruzar contra nuestro motor.

Lee un SELECT (T-SQL) de un fichero y emite JSON { columna_salida: [fuentes...] }, donde
cada fuente es "tabla.columna" en minúsculas. Solo cubre consultas (sqlglot es query-only;
no hace MERGE/INSERT/UPDATE/DELETE/procs - ahí manda nuestro motor ScriptDOM).

Uso (sin instalar nada permanente):
    uv run --with sqlglot python sqlglot_lineage.py <fichero.sql>
"""
import json
import sys

import sqlglot
from sqlglot.lineage import lineage

DIALECT = "tsql"


def leaves(node):
    """Nombres de las hojas (columnas raíz) aguas abajo de un nodo de lineage."""
    out = []

    def walk(n):
        if not n.downstream:
            out.append(n.name)
        for d in n.downstream:
            walk(d)

    for d in node.downstream:
        walk(d)
    return out


def main(path):
    sql = open(path, "r", encoding="utf-8-sig").read()
    try:
        tree = sqlglot.parse_one(sql, dialect=DIALECT)
        cols = tree.named_selects  # nombres de columnas de salida
    except Exception as e:  # noqa: BLE001
        print(json.dumps({"_error": f"{type(e).__name__}: {e}"}))
        return

    result = {}
    for col in cols:
        try:
            node = lineage(col, sql, dialect=DIALECT)
            result[col] = sorted({s.lower() for s in leaves(node)})
        except Exception as e:  # noqa: BLE001
            result[col] = [f"_error: {type(e).__name__}"]
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main(sys.argv[1])
