# Corpus de malas prácticas + evaluación del analizador

Base de datos T-SQL *boilerplate* deliberadamente cargada de **anti-patrones**, con
un **ground-truth** alineado a las reglas reales del analizador
([`dashboard/src/risks.js`](../../dashboard/src/risks.js)) y un **comparador
automático** que verifica detección esperada vs. real.

Sirve para tres cosas:

1. **Cubrir cada regla** del rule engine con un caso mínimo y aislado.
2. **Evaluar** el analizador de forma reproducible (gate de CI): ¿detecta lo que
   debe, sin ruido?
3. **Comparar** versiones del analizador entre sí (regresiones de detección).

> Datasets "buenos" de referencia (los que **no** deben generar ruido) están en
> `database/*.bak` (WideWorldImporters, AdventureWorks). Este corpus es el opuesto:
> el caso "todo mal", con la respuesta correcta escrita al lado.

## Estructura

| Archivo | Qué es |
|---|---|
| `sql/*.sql` | El corpus. Un `CREATE` por fichero (como exige `from-sql`). Cada cabecera documenta la **regla objetivo**. |
| `expected-findings.json` | **Ground-truth**: por componente, los hallazgos (`rule` + `sev` + `cat`) que el analizador *debe* producir. `expected: []` = control sin hallazgos (anti-falsos-positivos). |
| `evaluate.mjs` | Comparador headless. Reutiliza `shape.js` + `risks.js` del dashboard (sin duplicar lógica) y reporta `FALTAN` / `SOBRAN` / `SEV/CAT`. Sale `!=0` si hay discrepancias. |
| `run.ps1` / `run.sh` | Pipeline completo: `from-sql` → `graph` → `evaluate`. |

## Cómo ejecutarlo

```powershell
# Windows
cd eval/bad-practices
./run.ps1
```

```bash
# Linux/macOS
cd eval/bad-practices
./run.sh
```

O por pasos:

```bash
dotnet run --project ../../src/TSqlParser -- from-sql BadPracticesDB input.json sql/*.sql
dotnet run --project ../../src/TSqlParser -- input.json graph_full.json --columns
node evaluate.mjs graph_full.json expected-findings.json
```

El `graph_full.json` resultante también se puede subir tal cual a
`dashboard/index.html` para inspeccionar los hallazgos en el panel de **Riesgos**.

### Salida para agente / CI (`--json`)

Para que un **agente o un job de CI lo interprete como salida de prueba**, el
comparador acepta `--json`: en vez del texto coloreado, emite un informe
estructurado en stdout (el exit code es idéntico: `0` pasa, `1` discrepancias).

```bash
node evaluate.mjs graph_full.json expected-findings.json --json
```

```jsonc
{
  "pass": true,
  "summary": { "ok": 38, "missing": 0, "unexpected": 0, "sevCat": 0, "total": 38 },
  "components": [
    { "component": "dbo.Orders", "control": false, "inGroundTruth": true,
      "checks": [ { "rule": "Acoplamiento alto (tabla)", "status": "OK",
                    "expected": { "sev": "med", "cat": "Diseño" },
                    "actual":   { "sev": "med", "cat": "Diseño" } } ] }
  ]
}
```

`status` por check: `OK` · `MISSING` (esperado, no detectado) · `UNEXPECTED`
(detectado, no esperado) · `SEV_CAT` (regla correcta, severidad/categoría
distinta). Un agente puede leer `pass`/`summary` para decidir, o recorrer
`components[].checks` para localizar exactamente qué regla falló y en qué objeto.

## Cobertura de reglas

| # | Regla (`risks.js`) | Sev | Categoría | Caso que la dispara |
|---|---|---|---|---|
| 1 | Inyección SQL | crit | Seguridad | `usp_SearchCustomers_Injection` (dato de tabla → `EXEC(@sql)`) |
| 2 | SQL dinámico | high | Seguridad | `usp_DynamicReport` (`@sql` desde parámetros) |
| 3 | Cursor en transacción sin TRY/CATCH | high | Robustez | `usp_ProcessQueue_CursorTx` |
| 4 | Transacción sin TRY/CATCH | high | Robustez | `usp_TransferFunds_TxNoCatch` |
| 5 | Escritura sin protección | med | Robustez | `usp_QuickUpdate_NoProtection` (+ varios escritores) |
| 6 | Uso de cursor | med | Rendimiento | `usp_ProcessQueue_CursorTx` |
| 7 | Prefijo `sp_` | med | Rendimiento | `sp_GetActiveCustomers` |
| 8 | Escrituras repetidas a la misma tabla | med | Rendimiento | `usp_LogEverything_RepeatWrites` (3× `OrderAudit`) |
| 9 | Complejidad alta | med | Mantenibilidad | `usp_MegaWorkflow_Complex` (cc ≥ 10) |
| 10 | Anidación profunda | med | Mantenibilidad | `usp_MegaWorkflow_Complex` (4 niveles) |
| 11 | Variable sin uso | low | Mantenibilidad | `usp_ArchiveOldOrders_UnusedVars` |
| 12 | Posible código muerto | low | Mantenibilidad | `ufn_CalcDiscount` (función sin llamadores) |
| 13 | Objeto hace demasiado | med | Diseño | `usp_MegaWorkflow_Complex` (≥ 5 tablas) |
| 14 | Error de parseo | info | Mantenibilidad | `usp_Broken_ParseError` |
| 15 | Tabla sin clave primaria | med | Integridad | `OrderAudit` |
| 16 | Acoplamiento alto (tabla) | med | Diseño | `Orders` (≥ 4 escritores) |
| 17 | Tabla escrita pero nunca leída | low | Diseño | `OrderAudit` |
| 18 | UPDATE/DELETE sin WHERE | high | Integridad | `usp_PurgeAll_NoWhere` (mutación masiva sin filtro) |
| 19 | TRUNCATE de tabla | med | Integridad | `usp_TruncateAudit` |
| 20 | SELECT * | low | Rendimiento | `usp_DumpCustomers_SelectStar` |
| 21 | Tabla totalmente anulable | low | Integridad | `OrderAudit` (todas las columnas NULL) |
| 22 | Tabla ancha | low | Diseño | `WideProductCatalog` (14 columnas) |

Controles (deben quedar **limpios**): `Customers`, `SearchConfig`.

Las reglas 18-22 se añadieron junto a este corpus. `SELECT *` (regla 20) necesitó
además propagar un flag nuevo desde el parser: `AstWalker` ya detectaba el
`SelectStarExpression` pero no llegaba al grafo → se añadió `FlowLinkInfo.SelectStar`
→ propiedad `select_star` del Step → `shape.js` → regla en `risks.js`. Las reglas
18-19 y 21-22 usan datos que el grafo ya exponía (filtros del step, `op` de la
escritura, nulabilidad de columnas, conteo de columnas). `NOLOCK`/hints de tabla
quedan pendientes: requieren parsear los table hints, que hoy el parser ignora.

## Bug que destapó este eval (y su fix)

La primera ejecución dio `OK=12 / FALTAN=10 / SOBRAN=9`: **no era ruido**, sino un
bug real del flujo `from-sql` que el harness cazó (justo su propósito).

**Síntoma:** `0 READS_FROM` y casi `0 WRITES_TO` en todo el grafo; tablas con PK
inline marcadas como "sin clave primaria"; faltaban casi todas las reglas que
dependen de lecturas/escrituras de tabla.

**Causa raíz (una sola):** el router de `AnalyzeInput`
([Program.cs](../../src/TSqlParser/Program.cs)) decidía "¿es CREATE TABLE?" con un
regex anclado a `^`. Un **comentario de cabecera** antes del `CREATE TABLE` (como
los de este corpus) lo defendía → el fichero se enrutaba a *análisis de objeto* en
vez de a `TableAnalyzer`. Consecuencias en cadena:
- la PK inline no se extraía (la leía `TableAnalyzer`, que nunca se invocaba);
- la "tabla-como-objeto" entraba en `byPlainName`
  ([GraphExporter.cs:96](../../src/TSqlParser/GraphExporter.cs#L96)), así que los
  `SELECT/INSERT/UPDATE` contra ella se emitían como `TARGETS` (arista tipo
  llamada) en vez de `READS_FROM`/`WRITES_TO`
  ([GraphExporter.cs:458](../../src/TSqlParser/GraphExporter.cs#L458)) → lineage de
  tabla perdido.

**Fix:** `StripLeadingComments` antes del router (tolera `--`, `/* */` y espacios
iniciales), de modo que se decide sobre la primera sentencia real. Tras el fix:
`from-sql` reconoce las 4 tablas como esquemas (`TableAnalyzer`) y la evaluación
queda en **`OK=28 / FALTAN=0 / SOBRAN=0`**. Material directo de la prioridad nº 1
del toolkit (completitud de extracción).

> Nota: las tablas `Inventory`/`Notifications`/`Shipments` se referencian en
> `usp_MegaWorkflow_Complex` pero **nunca se declaran** (`CREATE TABLE`). El
> analizador las materializa desde la lista de columnas del `INSERT` y las marca
> sin-PK + solo-escritura: es un hallazgo legítimo (DDL ausente del análisis), y
> el ground-truth lo recoge como esperado.

## Mantener el ground-truth

Si cambias una regla en `risks.js` o añades/editas un `.sql`, actualiza
`expected-findings.json` en consecuencia. El comparador es estricto a propósito:
cualquier `FALTA` o `SOBRA` hace fallar la evaluación, de modo que el corpus
documenta el comportamiento *deseado*, no el actual.
