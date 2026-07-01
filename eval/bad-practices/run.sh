#!/usr/bin/env bash
# Pipeline de evaluación del corpus de malas prácticas (Linux/macOS).
#   from-sql -> graph -> evaluate (real vs. ground-truth)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
proj="$here/../../src/TSqlParser"
input="$here/input.json"
graph="$here/graph_full.json"

echo "== 1/3  from-sql =="
dotnet run --project "$proj" -- from-sql BadPracticesDB "$input" "$here"/sql/*.sql

echo "== 2/3  graph =="
dotnet run --project "$proj" -- "$input" "$graph" --columns

echo "== 3/3  evaluate =="
node "$here/evaluate.mjs" "$graph" "$here/expected-findings.json"
