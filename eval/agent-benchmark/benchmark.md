# Benchmark — Análisis de Impacto para Estrategia de Mejora

> Comparativa de agente **ciego** (grafo completo) vs. agente **informado**
> (nodestore precomputado) ante una petición de cambio real con impacto en
> lógica de negocio crítica.
>
> Base: **WideWorldImporters** (SQL Server, muestra pública Microsoft)

---

## La petición de negocio

El equipo de Cumplimiento emite la siguiente alerta:

> **CR-2026-047 — DataLoadSimulation: riesgo de auditoría permanente**
>
> El proceso de simulación de carga de datos desactiva la auditoría temporal
> (system-time) sobre tablas de negocio. Si el proceso falla a mitad, esas
> tablas quedan **sin auditoría indefinidamente** porque la reactivación no
> se ejecuta y no hay rollback.
>
> Necesitamos:
> 1. Mapa completo de todo lo que se ve afectado si tocamos este proceso
> 2. Flujo exacto de la lógica de negocio de principio a fin
> 3. Qué objetos son más peligrosos de cambiar y por qué
> 4. Un plan ordenado y seguro de refactorización por fases
> 5. Qué partes no se pueden analizar sin ejecutar el código

**Entregable**: informe de auditoría técnica con estrategia de mejora.

---

## Track A — Agente con grafo completo

### Lo que sabe el agente

Tienes acceso a un único fichero JSON: `out/graph_full.json`

Este fichero contiene el análisis completo de la base de datos WideWorldImporters
extraído por un parser T-SQL. Incluye todos los objetos (procedimientos, funciones,
triggers, vistas, tablas) con sus propiedades, y todas las relaciones entre ellos
(llamadas entre procedimientos, qué tablas lee o escribe cada objeto, qué columnas
derivan de qué otras, etc.).

### Tarea

Responde al CR-2026-047 produciendo el informe en:
`eval/agent-benchmark/results/track-A-result.md`

El informe debe cubrir los cinco puntos de la petición de negocio con el máximo
detalle que puedas obtener del fichero.

---

## Track B — Agente con nodestore

### Lo que sabe el agente

Tienes acceso a tres ficheros del nodestore generado por el mismo parser:

**`out/graph_full.nodes/model.json`**
Visión macro de la base de datos: lista de objetos con sus métricas clave
(complejidad ciclomática, grado de conexión, pasos de SQL dinámico), las aristas
entre ellos (quién llama a quién, qué escribe, qué lee), y los **workflows**
precomputados: cadenas de llamada completas desde los puntos de entrada hasta las
hojas.

**`out/graph_full.nodes/audit_report.json`**
Análisis de salud de la base de datos: hotspots (objetos con mayor score de riesgo
por conectividad + complejidad), blind spots (objetos sin referencias entrantes o
con SQL dinámico sin resolver), tablas sin lectores ni escritores, cobertura de
linaje de columnas, patrones de riesgo transversales, e **impacto precomputado**
por objeto (blast radius vía llamadas + vía datos).

**`out/graph_full.nodes/objects/<id>/object.json` y `nav.json`**
Detalle de un objeto concreto: flujo de control paso a paso, parámetros,
variables locales, y vecinos directos. Úsalos cuando necesites profundizar en un
objeto específico que hayas identificado como relevante.

### Tarea

Responde al CR-2026-047 produciendo el informe en:
`eval/agent-benchmark/results/track-B-result.md`

El informe debe cubrir los mismos cinco puntos. Añade una sección final:

**Sección extra — Qué aportó el nodestore**
Qué información obtuviste directamente de los ficheros precomputados sin tener que
recorrer el grafo manualmente, y qué te habría costado más trabajo desde
`graph_full.json`.

---

## Formato de salida (ambos tracks)

```markdown
# Informe CR-2026-047 — DataLoadSimulation [Track A|B]

## 1. Blast radius — Todo lo afectado
[Tabla con objeto, tipo, cómo se ve afectado (directo/transitivo/datos)]

## 2. Flujo de negocio — Cadena de llamadas
[Árbol o lista indentada de la cadena completa]

## 3. Evaluación de riesgo por objeto
[Tabla: objeto | CC | SQL dinámico | riesgo | justificación]

## 4. Plan de refactorización por fases
[Fases ordenadas: qué cambiar, en qué orden, por qué ese orden, validación]

## 5. Límites del análisis estático
[Qué no se puede saber sin ejecutar el código, y por qué]

## Métricas del agente
- Ficheros leídos:
- Llamadas a herramientas aproximadas:
- Estrategia de búsqueda usada:
```

---

## Rúbrica de evaluación (para el evaluador humano)

Puntúa cada track sobre 100. Usa el Ground Truth de la sección siguiente.

| # | Criterio | Pts | Lo que debe aparecer |
|---|----------|-----|----------------------|
| R1 | Entry point correcto | 10 | `PopulateDataToCurrentDate` como punto de entrada real |
| R2 | Blast radius completo | 20 | Los 6 objetos exactos en la cadena de llamadas |
| R3 | Tablas en riesgo | 15 | Las 17 tablas escritas por Deactivate/Reactivate |
| R4 | Vistas dependientes | 10 | `Website.Customers` y `Website.Suppliers` como afectadas |
| R5 | Métricas de riesgo | 15 | CC correcto por objeto, identifica SQL dinámico sin resolver |
| R6 | Límites estáticos | 10 | Procs instalados en runtime son incognoscibles estáticamente |
| R7 | Orden de refactorización | 15 | Bottom-up: hojas → operadores de tablas → orquestador → entry |
| R8 | Eficiencia | 5 | Track B usa menos llamadas para igual o mayor calidad |
| **Total** | | **100** | |

Escala por criterio: **0** ausente/incorrecto · **mitad** parcial · **máx** completo y correcto

---

## Ground Truth

> Solo para el evaluador. No mostrar al agente.

### Cadena de llamadas exacta

```
DataLoadSimulation.PopulateDataToCurrentDate                           ← entry point real
├── DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures [CC=21, score=462, SQL din. NO resuelto]
│   ├── DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad       [CC=19, score=399, escribe 17 tablas]
│   │   └── Application.Configuration_RemoveRowLevelSecurity
│   └── DataLoadSimulation.ReactivateTemporalTablesAfterDataLoad        [CC=2,  score=40,  escribe 17 tablas]
│       └── Application.Configuration_ApplyRowLevelSecurity
└── DataLoadSimulation.Configuration_RemoveDataLoadSimulationProcedures [SQL din. probable]
```

### 17 tablas en riesgo (escritas por Deactivate y Reactivate, idénticas en ambos)

Application.Cities · Application.Countries · Application.DeliveryMethods ·
Application.PaymentMethods · Application.People · Application.StateProvinces ·
Application.TransactionTypes · Purchasing.SupplierCategories · Purchasing.Suppliers ·
Sales.BuyingGroups · Sales.CustomerCategories · Sales.Customers ·
Warehouse.ColdRoomTemperatures · Warehouse.Colors · Warehouse.PackageTypes ·
Warehouse.StockGroups · Warehouse.StockItems

**Vistas que dependen de esas tablas**: `Website.Customers`, `Website.Suppliers`

### Métricas de riesgo por objeto

| Objeto | CC | SQL din. | Resuelto | Nivel |
|--------|----|----------|----------|-------|
| `PopulateDataToCurrentDate` | — | No | — | ALTO — orquestador sin TRY/CATCH |
| `Configuration_ApplyDataLoadSimulationProcedures` | 21 | Sí | **No** | **CRÍTICO** — instala procs en runtime |
| `DeactivateTemporalTablesBeforeDataLoad` | 19 | No | — | **CRÍTICO** — abre ventana de riesgo |
| `ReactivateTemporalTablesAfterDataLoad` | 2 | No | — | ALTO — cierra la ventana |
| `Configuration_RemoveDataLoadSimulationProcedures` | — | Probable | No | ALTO |
| `Configuration_RemoveRowLevelSecurity` | — | Sí | Parcial | MEDIO |
| `Configuration_ApplyRowLevelSecurity` | — | Sí | Parcial | MEDIO |

### Lo que NO es analizable estáticamente

`Configuration_ApplyDataLoadSimulationProcedures` lee `sys.procedures` y `sys.tables`
para descubrir en runtime qué procedimientos instalar. Los nombres de esos procs
solo se conocen al ejecutar. Son los auténticos blind spots del análisis.

### Orden correcto de refactorización

```
Fase 1 — Hojas sin riesgo de bloqueo
  · Application.Configuration_RemoveRowLevelSecurity  → añadir logging + idempotencia
  · Application.Configuration_ApplyRowLevelSecurity   → añadir logging + idempotencia

Fase 2 — Operadores de tablas temporales (primero el más simple)
  · ReactivateTemporalTablesAfterDataLoad (CC=2)
      → hacer idempotente (si ya está activa, no falla)
      → registrar qué tablas se reactivaron
  · DeactivateTemporalTablesBeforeDataLoad (CC=19)
      → guardar estado previo en tabla de auditoría propia
      → procesar por esquema (no 17 tablas de golpe)

Fase 3 — Orquestador
  · Configuration_ApplyDataLoadSimulationProcedures
      → envolver en TRY/CATCH con llamada a Reactivate en el CATCH
      → eliminar la instalación dinámica de procs (hacerla explícita)

Fase 4 — Entry point
  · PopulateDataToCurrentDate
      → transacción externa con ROLLBACK si la Fase 3 falla

Regla crítica: nunca cambiar Deactivate antes de tener Reactivate refactorizado
y probado, porque Deactivate abre la ventana de riesgo.
```
