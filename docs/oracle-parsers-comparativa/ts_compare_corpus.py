import json
import sys
from tree_sitter_language_pack import get_parser

parser = get_parser("sql")

def count_errors(node, total):
    count = 1 if (node.type == "ERROR" or node.is_missing) else 0
    total_nodes = 1
    for child in node.children:
        c, t = count_errors(child, total)
        count += c
        total_nodes += t
    return count, total_nodes

def main(input_json_path):
    with open(input_json_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    clean = 0
    with_errors = 0
    total_error_nodes = 0
    total_nodes_all = 0
    worst = []

    for obj in data:
        src = obj["Sql"].encode("utf-8")
        tree = parser.parse(src)
        errs, total_nodes = count_errors(tree.root_node, [0])
        total_error_nodes += errs
        total_nodes_all += total_nodes
        if errs == 0:
            clean += 1
        else:
            with_errors += 1
        worst.append((errs, obj["Name"], total_nodes))

    worst.sort(reverse=True)
    print(f"Objetos: {len(data)}  |  0 errores (tree-sitter): {clean}  |  con errores: {with_errors}")
    print(f"Nodos ERROR/missing totales: {total_error_nodes} de {total_nodes_all} nodos ({100*total_error_nodes/total_nodes_all:.1f}%)")
    print("\nPeores 10:")
    for errs, name, total_nodes in worst[:10]:
        print(f"  {errs:4d} errores / {total_nodes:5d} nodos  -  {name}")

if __name__ == "__main__":
    main(sys.argv[1])
