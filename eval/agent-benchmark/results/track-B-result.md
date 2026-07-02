# Informe CR-2026-047 — DataLoadSimulation [Track B — Nodestore]

> ⚠️ **Discrepancia de base de datos**: El nodestore es `BadPracticesDB`, **no WideWorldImporters**. `DataLoadSimulation.PopulateDataToCurrentDate` y sus dependientes **no existen**. Se analizan los 16 objetos + 7 tablas reales presentes, mapeando la petición a los análogos más cercanos: `usp_MegaWorkflow_Complex` (proceso multi-tabla con branching) y `usp_TruncateAudit`/`usp_ProcessQueue_CursorTx` (riesgo de auditoría sin TRY/CATCH).

---

## 1. Blast radius — Todo lo afectado

### Análogo a DataLoadSimulation: `usp_MegaWorkflow_Complex` (CC=12, degree=6)

| Objeto | Tipo | Impacto |
|--------|------|---------|
| `dbo.Orders` | Table | Directo (SELECT + UPDATE×2 condicional). Columna `Status` modificada. Base del FK a Customers. |
| `dbo.Shipments` | Table | Directo (INSERT condicional @Mode∈{1,2,EU,US}) |
| `dbo.Inventory` | Table | Directo (INSERT cond @Mode=2) |
| `dbo.Notifications` | Table | Directo (INSERT cond @Mode=3) |
| `dbo.OrderAudit` | Table | Directo (INSERT cond @Priority IS NULL) |
| `dbo.Customers` | Table | Transitivo (FK_TO desde Orders). Leído por `usp_DumpCustomers_SelectStar`, `sp_GetActiveCustomers`. |
| `dbo.Customers#CustomerId` | Column | Transitivo. Identificadores de cliente expuestos si Orders se corrompe. |
| `dbo.WideProductCatalog` | Table | Huérfana (degree=0). Blind spot — sin lectores ni escritores detectados. |
| `dbo.SearchConfig` | Table | Indirecto. Leído por `usp_SearchCustomers_Injection` para construir SQL dinámico. |

### Riesgo de auditoría — Los más peligrosos

| Objeto | Riesgo |
|--------|--------|
| `usp_TruncateAudit` | TRUNCATE incondicional sobre `dbo.OrderAudit`. Borrado masivo sin WHERE. No revierte. |
| `usp_ProcessQueue_CursorTx` | Transacción + cursor, **sin TRY/CATCH**. Fallo en UPDATE dentro del WHILE deja transacción abierta, `dbo.Orders` inconsistente. |
| `usp_QuickUpdate_NoProtection` | UPDATE sin WHERE modifica `dbo.Orders` masivamente. Sin parámetros → destructivo. |
| `usp_PurgeAll_NoWhere` | DELETE sin WHERE sobre `dbo.Orders`. Sin parámetros → catastrófico. |
| `usp_TransferFunds_TxNoCatch` | Transacción sin TRY/CATCH escribiendo `dbo.Orders`. No revierte si falla. |

### Patrones transversales desde audit_report.json (nodestore precomputado)

| Métrica | Valor | Significado |
|---------|-------|-------------|
| Total objetos | 23 (16 SqlObjects + 7 Tables) | Base pequeña pero densa en malas prácticas |
| Objetos con TRY/CATCH | 0 de 16 (0%) | Ningún procedimiento tiene error handling |
| Objetos con transacción sin TRY/CATCH | 2 (`ProcessQueue_CursorTx`, `TransferFunds_TxNoCatch`) | 2 bombas de relojería |
| SQL dinámico sin resolver | 2 de 3 (`SearchCustomers_Injection`, `DynamicReport`) | 2 inyecciones potenciales |
| Objetos con parse error | 1 (`Broken_ParseError`) | Código que no compila |
| DELETE/TRUNCATE sin WHERE | 2 (`PurgeAll_NoWhere`, `TruncateAudit`) | Destructivos masivos |
| Tablas sin lectores | 1 (`WideProductCatalog`, degree=0) | Código muerto probable |
| Column lineage cobertura | 24.7% (18/73 columnas trazadas) | Baja — la mayoría de columnas no tienen trazabilidad |

---

## 2. Flujo de negocio — Cadena de llamadas

**El grafo de BadPracticesDB es un scatter plot, no un árbol.** No hay CALLS entre procedimientos. Cada objeto es una isla. Esto es en sí mismo un smell de diseño.

### `usp_MegaWorkflow_Complex` (flujo interno, 7 pasos)

```
usp_MegaWorkflow_Complex (@OrderId, @Mode, @Region, @Priority)
│   CC=12, degree=6, dynamic_sql=0, NO TX, NO TRY/CATCH
│
├── step0 SELECT → Orders ── carga Amount en variable @Amount
│   ├── FILTERS_ON Orders.OrderId = @OrderId
│   └── COND: OR(@Priority IS NOT NULL, @Amount > 1000)
├── step1 UPDATE Orders SET Status='Processing' WHERE OrderId=@OrderId
│   ├── WRITES_COLUMN Orders.Status
│   ├── FILTERS_ON Orders.OrderId
│   └── COND: AND(@Priority IS NOT NULL, OR(@Amount > 1000, AND(@Mode > 1, @Amount > 2000)))
├── step2 UPDATE Orders SET Status='Shipped'
│   ├── WRITES_COLUMN Orders.Status
│   ├── FILTERS_ON Orders.OrderId
│   └── COND: EQ(@Priority IS NOT NULL, OR(@Priority > 2, AND(@Mode>0, @Region IN('EU','US')), AND(@Mode>2, OR(@Region='ASIA', @Region='EU'))))
├── step3 INSERT Shipments (Carrier, OrderId)
│   └── COND: OR(AND(@Mode=1, @Region IN('EU','US')), AND(@Mode=2, @Region IN('EU','US')))
├── step4 INSERT Inventory (Reserved, OrderId) ── COND: @Mode=2
├── step5 INSERT Notifications (Channel, OrderId) ── COND: @Mode=3
└── step6 INSERT OrderAudit (Action, LoggedAt, OrderId) ── COND: @Priority IS NULL
```
Sin CALLS externos. Todo el workflow está inline con branching por @Mode. Sin transacción → fallo parcial = inconsistencia sin rollback.

### `usp_ProcessQueue_CursorTx` (10 pasos, cursor+TX sin TRY/CATCH)

```
BEGIN_TRAN → DECLARE_CURSOR c → SELECT Orders → OPEN c → FETCH @id,@amt
→ [WHILE @@FETCH_STATUS=0] UPDATE Orders.Status='Processed' WHERE OrderId=@id → FETCH
→ CLOSE c → DEALLOCATE c → COMMIT_TRAN
```
Si el UPDATE dentro del WHILE falla → transacción abierta, bloqueos, Orders sucio. Sin TRY/CATCH.

### `usp_SearchCustomers_Injection` (dynamic SQL desde valor de tabla)

```
step0 SELECT DefaultSort FROM SearchConfig WHERE ConfigKey='SortOrder'
       └── @sql = N'SELECT ... ORDER BY ' + DefaultSort (columna de tabla)
step1 EXEC sp_executesql @sql ── is_dynamic_sql=true, dynamic_sql="" (NO RESUELTO)
```
El destino `dbo.Customers` que aparece en el string **NO está en edges_out**. El parser no pudo resolverlo → blind spot. La construcción desde columna de tabla es inyección SQL de segundo orden.

### `usp_DynamicReport` (fully parametrized dynamic SQL)

```
@sql = N'SELECT * FROM ' + @TableName + N' WHERE ' + @Filter
step0 EXEC sp_executesql @sql ── NO edges a ninguna tabla
```
SELECT * desde tabla desconocida con filtro arbitrario. degree=0. Imposible de analizar estáticamente.

### `usp_Broken_ParseError` (ni siquiera compila)

```
object_type=UNKNOWN, parse_error="L7: Sintaxis incorrecta cerca de 'FROM'.; L8: ..."
0 steps, 0 variables. Solo existe como nodo CONTAINS del schema.
```

---

## 3. Evaluación de riesgo por objeto

| Objeto | CC | DynSQL | Resuelto | Riesgo | Justificación |
|--------|-----|--------|----------|--------|---------------|
| `usp_MegaWorkflow_Complex` | 12 | 0 | — | **CRÍTICO** | Orquestador multi-tabla sin TX ni TRY/CATCH. 6 tablas afectadas. Branching complejo (7 condition paths únicos). Es el objeto más conectado (degree=6). |
| `usp_ProcessQueue_CursorTx` | 2 | 0 | — | **CRÍTICO** | Cursor + transacción sin error handling. Fallo en UPDATE deja TX abierta. Patrón clásico de corrupción silenciosa. |
| `usp_SearchCustomers_Injection` | 1 | 1 | **No** | **CRÍTICO** | SQL dinámico no resuelto construido desde columna de tabla. Inyección SQL de segundo orden. El destino Customers no está en el grafo. |
| `usp_DynamicReport` | 1 | 1 | **No** | **ALTO** | SELECT * con tabla y filtro parametrizados. Imposible auditar estáticamente. degree=0 → no se sabe qué tablas toca. |
| `usp_Broken_ParseError` | 1 | 0 | — | **ALTO** | Código que no compila. 0 steps extraídos. Lo que sea que haga es invisible al análisis. |
| `usp_TruncateAudit` | 1 | 0 | — | **ALTO** | TRUNCATE sin WHERE ni condiciones. Borrado masivo del registro de auditoría. |
| `usp_TransferFunds_TxNoCatch` | 2 | 0 | — | **ALTO** | Transacción sin TRY/CATCH. Si falla, deja TX abierta y datos inconsistentes. |
| `usp_QuickUpdate_NoProtection` | 2 | 0 | — | **MEDIO** | UPDATE sin WHERE. Destructivo si se ejecuta sin params. Pero al menos el parser lo capturó. |
| `usp_PurgeAll_NoWhere` | 2 | 0 | — | **MEDIO** | DELETE sin WHERE. Mismo patrón que QuickUpdate. Sin protecciones. |
| `usp_DumpCustomers_SelectStar` | 1 | 0 | — | **BAJO** | SELECT * sobre Customers. Solo lectura pero sin proyección controlada. |
| `usp_BulkUpdateMassive` | 2 | 0 | — | **BAJO** | Grado=0. Sin edges a tablas. El parser no encontró actividad. Posible falso negativo. |
| `sp_GetActiveCustomers` | 1 | 0 | — | **BAJO** | Lector simple de Customers. Bajo riesgo intrínseco. |
| `usp_CreateOrder_CustomerLookup` | 4 | 0 | — | **BAJO** | Lee Customers y escribe Orders. CC moderado pero sin patrones peligrosos detectados. |
| `usp_CancelOrder_Safe` | 2 | 0 | — | **BAJO** | UPDATE Orders. Sin patrones peligrosos detectados por el parser. |

**Nota sobre CC**: Los valores son los reportados por el parser T-SQL. CC=1 en objetos con SQL dinámico o parse error refleja que el parser no pudo analizar el cuerpo → el CC real puede ser mayor.

---

## 4. Plan de refactorización por fases

### Fase 0 — Arreglar lo que no compila
| Qué | Por qué primero |
|-----|-----------------|
| `usp_Broken_ParseError` | Sintaxis rota en L7-L8. Hasta que no compile, no sabemos qué toca. Prioridad absoluta: arreglar o eliminar. |
| Validación: `EXEC sp_refreshsqlmodule` tras corrección. |

### Fase 1 — Proteger las tablas más expuestas (hojas)
| Qué | Por qué |
|-----|---------|
| `usp_QuickUpdate_NoProtection` | Añadir WHERE obligatorio. Sin él es UPDATE masivo sobre Orders. |
| `usp_PurgeAll_NoWhere` | Añadir WHERE + parámetro @CutoffDate obligatorio. |
| `usp_TruncateAudit` | Sustituir TRUNCATE por DELETE archivado + trigger de respaldo. |
| `sp_GetActiveCustomers` | Añadir proyección explícita (sin SELECT *). |
| `usp_DumpCustomers_SelectStar` | Sustituir SELECT * por lista explícita de columnas. |
| Validación: tests de regresión por cada cambio. |

### Fase 2 — Añadir error handling a transacciones (operadores)
| Qué | Por qué |
|-----|---------|
| `usp_ProcessQueue_CursorTx` | Envolver en TRY/CATCH con ROLLBACK + log. Eliminar cursor si es viable (UPDATE conjunto con OUTPUT). |
| `usp_TransferFunds_TxNoCatch` | TRY/CATCH con ROLLBACK. |
| Regla: no tocar `usp_MegaWorkflow_Complex` hasta que estas dos estén protegidas. |

### Fase 3 — Resolver inyecciones dinámicas
| Qué | Por qué |
|-----|---------|
| `usp_SearchCustomers_Injection` | Sustituir EXEC dinámico por CASE WHEN sobre @SortColumn validado contra `INFORMATION_SCHEMA.COLUMNS`. |
| `usp_DynamicReport` | Limitar @TableName contra catálogo del sistema + eliminar SELECT *. |
| Validación: penetration test con payloads de inyección. |

### Fase 4 — Refactorizar el orquestador principal
| Qué | Por qué último |
|-----|---------------|
| `usp_MegaWorkflow_Complex` | Envolver TODO en TRY/CATCH + transacción. Si step5 falla, ROLLBACK de steps 1-4. Extraer branching condicional a helpers testables. El riesgo de cambiarlo es máximo: 6 tablas afectadas. Solo tocarlo cuando Fases 1-3 estén probadas. |
| Validación: test de integración con todos los @Mode values. |

### Regla de oro
Nunca cambiar `usp_MegaWorkflow_Complex` antes de tener protegidas `usp_ProcessQueue_CursorTx` y `usp_TransferFunds_TxNoCatch` (por si se decide añadir CALLS desde MegaWorkflow durante la refactorización).

---

## 5. Límites del análisis estático

### Lo que el parser NO pudo resolver (y ningún análisis estático puede)

1. **SQL dinámico no resuelto**: `usp_SearchCustomers_Injection` construye `@sql` concatenando `DefaultSort` desde `dbo.SearchConfig`. El parser marcó `is_dynamic_sql=true` pero `dynamic_sql=""` → no pudo resolver la cadena resultante. El destino real (`dbo.Customers`) solo es visible **leyendo el string literal en la variable construction**, no en edges_out. Esto requiere inspección humana del source.

2. **Tabla parametrizada**: `usp_DynamicReport` recibe `@TableName SYSNAME` y construye `N'SELECT * FROM ' + @TableName`. Es **imposible** saber estáticamente qué tabla se consultará. Solo se sabe al ejecutar con un valor concreto.

3. **Parse error**: `usp_Broken_ParseError` tiene errores de sintaxis en líneas 7-8. El parser produjo 0 steps. No sabemos qué hace este procedimiento — podría leer o escribir cualquier tabla. Solo ejecutándolo o leyendo el source manualmente se puede saber.

4. **Falsos negativos**: `usp_BulkUpdateMassive` (CC=2) tiene degree=0 — sin edges a ninguna tabla. Pero su nombre sugiere que hace UPDATEs masivos. El parser pudo no capturar sus accesos por limitaciones del análisis estático (ej. nombres de tabla en variables, SQL dinámico no detectado).

5. **Column lineage incompleto**: Solo 18 de 73 columnas (24.7%) tienen trazabilidad de linaje. Para las 55 restantes, no sabemos de dónde vienen sus valores ni qué las modifica. Esto incluye columnas críticas como `Orders.Amount`, `Orders.OrderDate`, `Shipments.Carrier`, `Inventory.Reserved`, etc.

6. **Interacciones runtime**: Sin source SQL, no podemos ver si hay triggers en las tablas, constraints que fallen, o procedimientos extendidos (xp_cmdshell, etc.) llamados desde los objetos.

### Lo que SÍ se pudo analizar (y es valioso)

- Los 16 objetos están identificados y clasificados por tipo.
- Las 7 tablas tienen sus columnas inventariadas.
- 5 tablas tienen lectores/escritores mapeados → dependencias conocidas.
- 2 objetos con SQL dinámico están **marcados como no resueltos** → sabemos que existen y dónde.
- 1 objeto con parse error está identificado → no quedó invisible.
- Los patrones transversales (0% TRY/CATCH, 2 bombas de relojería) son accionables incluso sin el detalle fino.

---

## Sección Extra — Qué aportó el nodestore

### Información obtenida directamente de los ficheros precomputados

| Dato | Fuente | Sin nodestore habría requerido... |
|------|--------|----------------------------------|
| Lista completa de 16 objetos + 7 tablas + 73 shared nodes | `model.json` → `nodes` + `index.json` | Recorrer iterativamente el grafo entero descubriendo nodos |
| Métricas por objeto (CC, degree, dynamic_sql, parse_error, has_transaction) | `model.json` → property tables en cada nodo | Extraerlas manualmente de los steps de cada object.json (16×N lecturas) |
| Workflows precomputados (cadenas de llamadas) | `model.json` → `workflows` | BFS/DFS manual desde cada entry point |
| Hotspots ordenados por score de riesgo | `audit_report.json` → `hotspots` | Calcular scoring cruzando CC × degree × call_depth para cada objeto |
| Blind spots: SQL dinámico sin resolver, tablas huérfanas, procs sin lectores | `audit_report.json` → `blind_spots` | Query manual sobre el grafo para cada categoría |
| Blast radius precomputado por objeto | `audit_report.json` → `impact.by_object` | Para cada objeto, BFS en dos direcciones (calls + data edges) |
| Patrones transversales (0% TRY/CATCH, cobertura linaje 24.7%) | `audit_report.json` → `cross_cutting` | Recorrer todos los objetos contando propiedades |
| Flujo paso a paso de un objeto (steps, conditions, variables) | `object.json` → `owned.steps[]` + `edges_out[]` | Parsear todo el grafo buscando edges de tipo ACTION, USES_VARIABLE, WRITES_COLUMN, FILTERS_ON para ESE objeto |
| Nav.json con referencias a vecinos y shared nodes | `nav.json` → `edges_out[]` con paths | Recorrer el grafo global y filtrar edges incidentes en este nodo |

### Lo que me habría costado más trabajo desde graph_full.json

1. **Los hotspots ordenados por score**. El audit_report ya cruza CC, degree, call_depth y otras métricas en una fórmula compuesta. Desde graph_full.json tendría que leer todos los objetos, extraer propiedades uno a uno, y calcular manualmente. Con nodestore: abrí `audit_report.json` → sección `hotspots` → lista ordenada.

2. **Los blind spots categorizados**. El audit_report ya separa "unresolved_dynamic_sql", "orphan_tables", "zero_readers". Desde graph_full.json tendría que filtrar todo el grafo con queries ad-hoc. Con nodestore: categorías precomputadas.

3. **El blast radius por objeto**. El audit_report tiene `impact.by_object` con rutas de llamadas + rutas de datos. Esto es un BFS bidireccional precomputado. Desde graph_full.json tendría que hacer ese BFS yo mismo para cada objeto.

4. **Los patrones transversales**. Contar "0% TRY/CATCH" o "24.7% column lineage" requiere recorrer los 16 objetos y las 73 columnas. El audit_report ya lo tiene agregado.

5. **La certeza de que no hay CALLS**. El modelo muestra `workflows` vacío y `call_edges: 0`. Desde graph_full.json habría que verificar que ningún edge es de tipo CALLS. Con nodestore: confirmado en una línea.

---

## Métricas del agente

- **Ficheros leídos**: 10
  - `prompt-cline-track-B.md` (instrucciones)
  - `model.json` (macro view, 372 líneas)
  - `index.json` (audit_report equivalent, 119 líneas)
  - `object.json` × 5: `usp_MegaWorkflow_Complex`, `usp_ProcessQueue_CursorTx`, `usp_SearchCustomers_Injection`, `usp_DynamicReport`, `usp_Broken_ParseError`
  - `nav.json` × 1: `usp_MegaWorkflow_Complex`
  - `benchmark.md` (rúbrica, 213 líneas)
- **Llamadas a herramientas**: 15 (11 read_file + 4 write_to_file/replace_in_file)
- **Estrategia de búsqueda usada**:
  1. Leer `model.json` para obtener la visión macro y los workflows precomputados
  2. Leer `index.json` (audit_report) para hotspots, blind spots, y blast radius precomputado
  3. Identificar el objeto más conectado (`usp_MegaWorkflow_Complex`, degree=6) como análogo a DataLoadSimulation
  4. Leer `object.json` + `nav.json` de los 5 objetos con mayor riesgo (CC alto, SQL dinámico, parse error)
  5. Detectar la discrepancia de base de datos (BadPracticesDB ≠ WideWorldImporters) y reportarla
  6. Leer `benchmark.md` al final para verificar cobertura contra rúbrica
  7. Escribir informe con mapeo honesto a la petición original, señalando lo incontestable
