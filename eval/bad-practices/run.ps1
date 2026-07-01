# Pipeline de evaluación del corpus de malas prácticas (Windows / PowerShell).
#   1. from-sql : construye input.json a partir de los .sql del corpus
#   2. graph    : genera graph_full.json (con columnas, para reglas de tabla)
#   3. evaluate : compara hallazgos reales vs. ground-truth (expected-findings.json)
#
# Uso:  .\run.ps1            (desde eval/bad-practices)
$ErrorActionPreference = 'Stop'
$here    = $PSScriptRoot
$proj    = Resolve-Path (Join-Path $here '..' '..' 'src' 'TSqlParser')
$sqlGlob = Join-Path $here 'sql' '*.sql'
$input   = Join-Path $here 'input.json'
$graph   = Join-Path $here 'graph_full.json'

Write-Host '== 1/3  from-sql ==' -ForegroundColor Cyan
dotnet run --project $proj -- from-sql BadPracticesDB $input $sqlGlob

Write-Host '== 2/3  graph ==' -ForegroundColor Cyan
dotnet run --project $proj -- $input $graph --columns

Write-Host '== 3/3  evaluate ==' -ForegroundColor Cyan
node (Join-Path $here 'evaluate.mjs') $graph (Join-Path $here 'expected-findings.json')
exit $LASTEXITCODE
