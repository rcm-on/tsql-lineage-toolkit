import json
import re
import sys
import sqlglot
from sqlglot.errors import ParseError

DYN_MARKERS = re.compile(r"\bEXEC(UTE)?\s*\(|sp_executesql|@SQL\b|@sql\b", re.IGNORECASE)

def main(input_json_path):
    with open(input_json_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    hard_dyn, hard_other = [], []
    command_dyn, command_other = [], []
    clean = 0

    for obj in data:
        has_dyn = bool(DYN_MARKERS.search(obj["Sql"]))
        try:
            statements = sqlglot.parse(obj["Sql"], read="tsql")
        except Exception as e:
            (hard_dyn if has_dyn else hard_other).append((obj["Name"], str(e).split(chr(10))[0][:90]))
            continue

        n_command = 0
        command_texts = []
        for s in statements:
            if s is None:
                continue
            for node in s.walk():
                if type(node).__name__ == "Command":
                    n_command += 1
                    command_texts.append(repr(getattr(node, "this", ""))[:40])

        if n_command > 0:
            is_dyn_related = any(re.search(r"SET|DROP TRIGGER|EXEC", t, re.IGNORECASE) for t in command_texts) and has_dyn
            (command_dyn if is_dyn_related else command_other).append((obj["Name"], n_command, command_texts[:3]))
        else:
            clean += 1

    print(f"Total: {len(data)}  |  limpios del todo: {clean}")
    print(f"\nERROR DURO relacionado con SQL dinamico: {len(hard_dyn)}")
    for n, m in hard_dyn: print(f"  {n}: {m}")
    print(f"\nERROR DURO SIN relacion con SQL dinamico (T-SQL basico): {len(hard_other)}")
    for n, m in hard_other: print(f"  {n}: {m}")
    print(f"\nCommand opaco, relacionado con SQL dinamico: {len(command_dyn)}")
    for n, c, t in command_dyn: print(f"  {n}: {c} nodos {t}")
    print(f"\nCommand opaco, SIN relacion con SQL dinamico: {len(command_other)}")
    for n, c, t in command_other: print(f"  {n}: {c} nodos {t}")

if __name__ == "__main__":
    main(sys.argv[1])
