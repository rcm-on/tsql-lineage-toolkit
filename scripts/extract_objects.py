"""
Extrae las definiciones SQL (procedimientos, funciones, triggers) de una base
de datos SQL Server en el formato `input.json` que espera TSqlParser:

    [{ "name": "Database::schema.objeto", "sql": "CREATE PROCEDURE ..." }, ...]

Uso:
    python extract_objects.py --db WideWorldImporters --server .\\SQLEXPRESS --output input.json
    python extract_objects.py --db AdventureWorks2019 --server .\\SQLEXPRESS --output input.json --trusted

Requiere: pip install pyodbc
"""
from __future__ import annotations
import argparse
import json
import sys

import pyodbc

DRIVERS_TO_TRY = [
    "ODBC Driver 18 for SQL Server",
    "ODBC Driver 17 for SQL Server",
    "SQL Server",
]

QUERY = """
SELECT s.name AS schema_name, o.name AS object_name, m.definition AS sql_definition
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'TR', 'V')
ORDER BY s.name, o.name;
"""


def connect(server: str, database: str, user: str | None, password: str | None) -> pyodbc.Connection:
    last_err: Exception | None = None
    for driver in DRIVERS_TO_TRY:
        auth = (
            f"UID={user};PWD={password};"
            if user
            else "Trusted_Connection=yes;"
        )
        conn_str = (
            f"DRIVER={{{driver}}};SERVER={server};DATABASE={database};{auth}"
            "TrustServerCertificate=yes;"
        )
        try:
            return pyodbc.connect(conn_str)
        except pyodbc.Error as exc:
            last_err = exc
    raise RuntimeError(f"No se pudo conectar con ningún driver ODBC probado: {last_err}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", required=True, help="Nombre de la base de datos")
    parser.add_argument("--server", default=r".\SQLEXPRESS", help="Instancia de SQL Server")
    parser.add_argument("--output", default="input.json", help="Ruta del input.json a generar")
    parser.add_argument("--user", default=None, help="Usuario SQL (si no, autenticación de Windows)")
    parser.add_argument("--password", default=None, help="Password SQL")
    args = parser.parse_args()

    conn = connect(args.server, args.db, args.user, args.password)
    try:
        cursor = conn.cursor()
        cursor.execute(QUERY)
        objects = [
            {
                "name": f"{args.db}::{row.schema_name}.{row.object_name}",
                "sql": row.sql_definition,
            }
            for row in cursor.fetchall()
            if row.sql_definition
        ]
    finally:
        conn.close()

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(objects, f, indent=2, ensure_ascii=False)

    print(f"Escritos {len(objects)} objetos en {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
