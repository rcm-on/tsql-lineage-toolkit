---
title: Auditoría de DNN Platform
description: Informe de auditoría end-to-end sobre el corpus DNN, generado con el motor y el MCP. Demo real de lo que el producto puede y no puede afirmar.
read_when: Para ver un informe completo de ejemplo, o antes de escribir uno nuevo.
related: [docs/auditoria-plantilla.md, docs/PROYECTO.md, eval/column-recall/blind-refs.md]
stability: volatile
updated: 2026-08-22
---

# Auditoría de DNN Platform

**Objetivo declarado**: toma de contacto con una base desconocida. Según
`docs/auditoria-plantilla.md` §1, eso abre tres categorías — **calidad**, **inventario** y
**puntos ciegos** — y deja fuera rendimiento e impacto de un cambio concreto.

**Procedencia de cada afirmación.** Cada hallazgo lleva una etiqueta:

| Etiqueta | Significa |
|---|---|
| **[motor]** | Sale del grafo. Reproducible con los comandos del anexo. |
| **[lectura]** | Lo obtuvo una persona leyendo el T-SQL, porque el motor no puede verlo. Va marcado precisamente para que no se confunda con lo anterior. |

---

## 1. Portada — alcance y procedencia

**[motor]** — de `store_info`:

| | |
|---|---|
| Base | `DnnCorpus` (DNN Platform, esquema congelado 2026-08-01) |
| Generado | 2026-08-22, **0 días de antigüedad** |
| Formato | `graph-sqlite-v1` |
| Nodos / aristas | 7.533 / 34.278 |
| Objetos | **739** — 682 procedimientos, 25 vistas, 24 funciones escalares, 8 funciones de tabla |
| Tablas / columnas | 165 / 1.791 |
| Errores de parseo | **0 de 739** |

Cobertura de linaje de columna medida contra el catálogo real de SQL Server:
**99,6987 %**, 22 referencias ciegas de 7.302. El reparto por causa de esas 22 está en
`eval/column-recall/blind-refs.md`; no se repite aquí.

**Límite del alcance, dicho aquí y no en un anexo**: el corpus es **solo el esquema de base
de datos**. La aplicación .NET que llama a estos procedimientos no está en el grafo. Por eso
**710 de 739 objetos aparecen sin llamadores**, y eso **no es un hallazgo**: es el borde del
corpus. Un informe que lo listara como "código muerto" estaría mintiendo con datos ciertos.

---

## 2. Lo que NO se ha podido examinar

Va antes que los hallazgos, no al final.

### 2.1 SQL dinámico sin resolver — 3 objetos

**[motor]** — de `blind_spots`: 3 objetos tienen SQL dinámico que nunca resolvió a texto
literal. Son **todo** el SQL dinámico del corpus: 3 pasos dinámicos de 1.419 pasos totales.

Que sean solo 3 está verificado por un camino independiente del motor: una búsqueda de
`sp_executesql` / `EXEC(` sobre el texto fuente del corpus devuelve **exactamente los mismos
tres nombres**. No hay subconteo.

| Objeto | `total_steps` | Complejidad | Aristas `READS_FROM` | Tablas distintas |
|---|---:|---:|---:|---:|
| `dbo.GetUsersBasicSearch` | **1** | **1** | **0** | **0** |
| `dbo.GetUsersAdvancedSearch` | 11 | 13 | 6 | 3 |
| `dbo.GetAvailableUsersForIndex` | 2 | 1 | 2 | 2 |

(Las dos últimas columnas cuentan cosas distintas: un objeto puede leer la misma tabla desde
varios pasos. §2.2 compara **tablas distintas**, que es lo comparable con el texto fuente.)

**El caso que hay que entender antes de leer el resto del informe**:
`dbo.GetUsersBasicSearch` son ~100 líneas de T-SQL que el grafo reduce a **un solo paso**
(`step0`, línea 102, `EXEC → (dynamic SQL)`), con complejidad ciclomática **1** y **cero**
aristas de lectura o escritura. Por toda métrica de calidad de este informe es el objeto
**más simple del corpus**. Y es el que más superficie oculta.

Quien consulte el grafo sin leer esta sección primero concluirá "procedimiento trivial, sin
impacto". Es exactamente el fallo que la herramienta `blind_spots` existe para impedir.

Detalle del formato, por si alguien lo audita: en esos pasos el campo `dynamic_sql` viene
**vacío**, no truncado. Vacío significa "no resolvió", y es consistente: en el store de
WideWorldImporters, de 141 pasos dinámicos hay 138 con texto y 3 vacíos, y esos 3 son
exactamente los que el store cuenta como `unresolved_dynamic_sql_steps`. El campo se rellena
cuando la resolución funciona; aquí no funcionó ninguna vez.

### 2.2 La ruta seguida a mano

**[lectura]** — El motor no puede seguir dentro de la cadena dinámica, así que se leyeron
los tres procedimientos y se trazaron sus lecturas comparando las tablas citadas en el texto
con las que el motor registró:

| Objeto | Tablas que ve el motor | Tablas en el texto | **Ocultas** |
|---|---:|---:|---|
| `dbo.GetUsersBasicSearch` | 0 | 2 | `vw_Users`, `vw_Profile` |
| `dbo.GetUsersAdvancedSearch` | 3 | 8 | `vw_Users`, `vw_Profile`, `UserProfile`, `UserRoles`, `UserRelationships`, `Relationships` |
| `dbo.GetAvailableUsersForIndex` | 2 | 5 | `Users`, `UserPortals`, `vw_Profile` |

**11 lecturas de tabla invisibles para el grafo**, sobre 8 entidades distintas — entre ellas
`dbo.Users`, la segunda tabla más acoplada del corpus (91 lecturas registradas).

**Límite de este método, comprobado y no supuesto**: la traza se hizo buscando `FROM`/`JOIN`,
que se comería una escritura oculta. Se buscaron aparte `INSERT`/`UPDATE`/`DELETE`/`MERGE`/
`TRUNCATE` en los tres: las únicas que hay escriben en **variables de tabla**
(`@UserColumns`, `@PropertyNamesTable`, `@PropertyValuesTable`, `@UserFiltersTable`,
`@TempUsers`) y en una temporal (`#MatchingUsers`). **Ninguna toca una tabla persistida**, así
que el punto ciego de estos tres objetos es de lectura, no de escritura. Eso acota el daño:
pueden estar leyendo cualquier cosa, pero no modificando nada sin que se vea.

### 2.3 Consecuencia: el punto ciego contamina otra sección

**[motor + lectura]** — Este es el efecto de segundo orden que conviene mirar, porque
demuestra que un punto ciego no se queda quieto en su sección.

El inventario marca **18 tablas huérfanas** (sin lecturas ni escrituras). Una de ellas,
**`dbo.vw_Profile`**, tiene 0 lecturas y 0 escrituras en el grafo — y los **tres**
procedimientos de arriba la leen, dentro del SQL dinámico.

`dbo.vw_Profile` no está huérfana. Aparece huérfana **porque sus únicos lectores son
ciegos**. Cualquiera que use la lista de huérfanas para decidir qué retirar borraría una
vista en uso.

Las otras 17 huérfanas son vistas `vw_*` sin lectores dentro del corpus, lo que es
esperable: sus consumidores son la aplicación .NET, que no está en el grafo (§1).

### 2.4 Categorías declaradas no evaluables

- **Rendimiento**: no hay planes de ejecución para este store. Según
  `docs/auditoria-plantilla.md` §2, sin planes esta sección **no se escribe**. El análisis
  estático da indicios (cursores, anidamiento, `SELECT *`), no coste. Para habilitarla:
  `capture-plans` + `enrich-from-plans`.
- **Seguridad en sentido amplio**: permisos, `GRANT`, cifrado, datos personales y
  autenticación **no están en el grafo**. Lo que sí se puede auditar se llama *superficie de
  inyección* (§4) y no *seguridad*.

---

## 3. Inventario y calidad

**[motor]**

| Señal | Valor |
|---|---|
| Objetos con transacción | 6 de 739 |
| Objetos con `TRY`/`CATCH` | 3 de 739 |
| Objetos con cursor | 1 de 739 |
| Objetos con `SELECT *` en algún paso | **181 de 739** (24 %) |
| Cobertura de linaje de columnas de salida | 99,1 % (530 de 535) |

**Confianza de las aristas de columna** — el reparto importa porque decide qué se puede
afirmar sin reservas:

| `resolution` | Aristas | Qué significa |
|---|---:|---|
| `direct` | 6.077 | La columna está literal en el SQL. **Seguro.** |
| `star_expanded` | 4.327 | Deducida al expandir un `SELECT *`. **Probable.** |
| `via_view` | 3.340 | Deducida atravesando una vista. **Probable.** |

**El 56 % de las aristas de columna son deducciones, no lecturas literales.** No es un
defecto — es lo que permite tener linaje a través de `SELECT *` y de vistas — pero un plan de
cambio que trate las tres clases por igual está sobrevalorando su propia certeza. Los 181
objetos con `SELECT *` son el origen directo de la clase `star_expanded`.

### Puntos calientes

**[motor]** — de `audit_report.json`, por `score` (complejidad × grado de acoplamiento):

| Score | Objeto | Complejidad | Grado |
|---:|---|---:|---:|
| 78 | `dbo.GetUsersAdvancedSearch` | 13 | 6 |
| 51 | `dbo.FilePath` | 3 | 17 |
| 49 | `dbo.GetFoldersByPermissions` | 7 | 7 |
| 48 | `dbo.EnsureLocalizationExists` | 6 | 8 |
| 44 | `dbo.BuildTabLevelAndPath` | 4 | 11 |

`dbo.GetUsersAdvancedSearch` encabeza la lista **y** está en la lista de ciegos: es el único
objeto del corpus que es a la vez el más complejo y parcialmente invisible.

### Recursión

**[motor]** — El orden topológico de `CALLS` existe salvo por **3 ciclos**, los tres
auto-referencias (recursión directa):

- `dbo.BuildTabLevelAndPath`
- `dbo.UpdateFolderModifiedOnToCurrentDate`
- `dbo.GetListParentKey`

Se reportan como ciclo, no se ordenan arbitrariamente. **[lectura]** los tres son recursión
deliberada sobre jerarquías, verificada en el fuente: `BuildTabLevelAndPath` se llama con
`@ChildID` (línea 28) para bajar el árbol de páginas, `UpdateFolderModifiedOnToCurrentDate`
con `@ParentID` (línea 16) para subir el de carpetas, y `GetListParentKey` es una **función
escalar** que se invoca a sí misma con `@Count+1` (línea 43), con contador de profundidad.
No son un defecto.

---

## 4. Superficie de inyección

**[motor]** — 3 objetos ejecutan SQL dinámico; los tres reciben el hallazgo
`high / Seguridad / SQL dinámico`. Ninguno recibe `crit / Inyección SQL`.

**Ese cero es un falso negativo, y está localizado.** Ver §6.2: el motor tiene las dos
mitades de la prueba en el grafo y no las une.

**[lectura]** — Leídos los tres, la superficie real no es uniforme:

| Objeto | Parametrización | Valoración |
|---|---|---|
| `dbo.GetAvailableUsersForIndex` | `sp_executesql` **con parámetros tipados** para `@PortalId`, `@StartDate`, `@startUserId`, `@numberOfUsers` | Los parámetros de entrada están bien tratados. **El riesgo está en `@PivotSql`** (§6.2) |
| `dbo.GetUsersBasicSearch` | Concatena `@PropertyName` y `@PropertyValue` **directamente en el texto** de la consulta, incluido dentro de un literal `LIKE N'…%'` | Superficie de primer orden. Depende por completo de que el llamante valide; el grafo no puede saber si lo hace |
| `dbo.GetUsersAdvancedSearch` | Mismo patrón de construcción por concatenación | Igual que el anterior |

Lo que este informe **no** puede afirmar: si esos parámetros llegan de un formulario web sin
validar. Eso vive en la aplicación .NET, fuera del corpus. Se deja como pregunta al equipo,
no como hallazgo.

---

## 5. Hallazgos de riesgo

**[motor]** — `risks` sobre el store: **781 hallazgos**.

| Severidad | Nº |
|---|---:|
| `crit` | **0** (falso negativo, §6.2) |
| `high` | 11 |
| `med` | 467 |
| `low` | 303 |
| `info` | 0 |

Los 11 `high`, reproducidos regla a regla contra el store (3 + 4 + 4 = 11, cuadra con el
total que devuelve la herramienta):

| Regla | Nº | Objetos |
|---|---:|---|
| `Seguridad / SQL dinámico` | 3 | los tres de §2.1 |
| `Robustez / Transacción sin TRY/CATCH` | 4 | `AddHeirarchicalTerm`, `DeleteHeirarchicalTerm`, `UpdateHeirarchicalTerm`, `PurgeScheduleHistory` |
| `Integridad / UPDATE/DELETE sin WHERE` | 4 | `BuildTabLevelAndPath`, `DeleteEventLog`, `OutputCachePurgeCache`, `PurgeScheduleHistory` |
| `Robustez / Cursor en transacción sin TRY/CATCH` | 0 | — |

**Advertencia que el propio motor emite y que hay que respetar**: `datos_de_ejecucion: false`.
Sin planes, un objeto que corre dos veces al año y otro en el camino caliente reciben el
mismo veredicto. La severidad ordena **cómo de malo parece un patrón aislado**, no cuánto
duele.

### El hallazgo que sí hay que atender

`dbo.DeleteEventLog` — **[motor]** hallazgo `high`, **[lectura]** confirmado:

```sql
IF @LogGUID is null
BEGIN
    DELETE FROM dbo.EventLog        -- línea 7: sin WHERE
END ELSE BEGIN
    DELETE FROM dbo.EventLog WHERE LogGUID = @LogGUID
END
```

Un `@LogGUID` nulo **vacía el registro de eventos entero**. Es un `DELETE` sin filtro
alcanzable por un parámetro por defecto, no una purga declarada como tal. Es el único de los
cuatro hallazgos destructivos que combina "sin filtro" con "activable por accidente".

`dbo.OutputCachePurgeCache` también borra sin filtro, pero su nombre declara la intención
(purgar la caché) y la tabla es una caché reconstruible. Hallazgo correcto, riesgo bajo.

---

## 6. Auditoría del auditor

Dos defectos del motor encontrados **por esta auditoría**, no por sus pruebas. Van en el
informe porque un informe que no declara los límites del instrumento con el que se hizo vale
menos.

### 6.1 Falso positivo: "UPDATE/DELETE sin WHERE" — 2 de 4 · **ARREGLADO 2026-08-22**

> **Arreglado.** El paso lleva ahora una propiedad `has_where` propia, derivada de
> `FlowLinkInfo.FilterText` — el WHERE de verdad — en vez de deducirse de las aristas.
> Resultado sobre DNN: `BuildTabLevelAndPath` deja de aparecer, y **aparecen dos objetos que
> el defecto ocultaba en la dirección contraria**: `dbo.DeleteOrphanedAspNetUsers` (línea 22,
> `DELETE m FROM … INNER JOIN …`) y `dbo.CoreMessaging_GetNextMessagesForDigestDispatch`
> (línea 8, el `WHERE` está dentro de la subconsulta). Los dos emitían `FILTERS_ON` desde un
> `JOIN ... ON`, así que la regla vieja los daba por filtrados. El total `high` pasa de 11 a
> **12**: −1 falso positivo, +2 verdaderos que no se veían. Gate: `HasWhereRuleTests`, 6
> pruebas, 3 de ellas rojas con el criterio anterior.

El diagnóstico original, que se mantiene por trazabilidad:

**[motor + lectura]** — La regla marca un paso cuando **no tiene arista `FILTERS_ON`**, y usa
eso como sustituto de "no tiene `WHERE`". No son lo mismo.

| Objeto | Línea | Destino | `FILTERS_ON` | ¿`WHERE` en el fuente? | Veredicto |
|---|---:|---|---:|---|---|
| `BuildTabLevelAndPath` | 29 | `@ChildTabs` (variable de tabla) | 0 | **sí** (`WHERE TabID = @ChildID`) | **falso positivo** |
| `PurgeScheduleHistory` | 41 | `dbo.ScheduleHistory` | 0 | **sí** (`WHERE … IN (SELECT TOP (1000) …)`) | **falso positivo** |
| `DeleteEventLog` | 7 | `dbo.EventLog` | 0 | no | correcto |
| `OutputCachePurgeCache` | 4 | `dbo.OutputCache` | 0 | no | correcto |

**Causa raíz, una sola**: no se emite arista de filtro cuando el predicado solo referencia
entidades que el grafo **no modela a propósito** — tablas temporales `#x` y variables de
tabla `@x`. El `WHERE` existe; la arista no.

**Control interno que lo prueba**: `dbo.DeleteEventLog` tiene los dos casos en el mismo
procedimiento y sobre la misma tabla — `step0` sin `WHERE` (`FILTERS_ON=0`) y `step1` con
`WHERE` (`FILTERS_ON=1`). El mecanismo funciona perfectamente cuando el predicado toca una
columna modelada. El defecto es específicamente el de las entidades no modeladas.

Efecto sobre este informe: **`PurgeScheduleHistory` es, además, un purgado por lotes bien
escrito** — `DELETE` de 1.000 filas dentro de un `WHILE`, con transacción por lote. La
herramienta `evidence` lo enseña sin abrir el fichero: el paso cuelga de `WHILE#37`. Es lo
contrario de lo que decía el hallazgo.

### 6.2 Falso negativo: `crit / Inyección SQL` — el taint no se propaga entre variables

**[motor]** — En `dbo.GetAvailableUsersForIndex` la ruta real es:

```
dbo.ProfilePropertyDefinition.PropertyName   (columna, dato de tabla)
        ↓ SELECT @PivotSql = … + '[' + PropertyName + ']' …      (línea 11)
   @PivotSql
        ↓ concatenación dentro del texto de @Sql                  (línea 66)
   @Sql
        ↓ EXECUTE sp_executesql @Sql                              (línea 69)
```

Que es exactamente el patrón que la regla `crit` busca: SQL dinámico construido desde datos
de tabla. Pero la regla exige que **una misma variable** cumpla las dos condiciones:

```csharp
var tainted = vars.Where(v => v.BuildsSql > 0 && v.AssignedFrom.Count > 0)
```

Y aquí están repartidas:

| Variable | `ASSIGNED_FROM` | `BUILDS_SQL_FROM` |
|---|---|---:|
| `@PivotSql` | `ProfilePropertyDefinition.PropertyName` ✔ | 0 |
| `@Sql` | — | 1 ✔ |

Ninguna cumple ambas → `crit = 0`.

**Por qué no puede cumplirlas hoy**: en las 34.278 aristas del grafo **no hay ni una sola
arista `Variable → Variable`**. `ASSIGNED_FROM` apunta siempre a `Column` (128 aristas, todas).
El flujo de datos entre variables no está modelado, así que el taint no puede propagarse por
construcción, no por un descuido de la regla.

**Alcance medido**: exactamente **1 objeto** del corpus tiene el patrón partido en dos
variables. No es una epidemia — es un falso negativo, localizado, con causa nombrada.

**Atenuante honesto**: `PropertyName` es un nombre de propiedad de perfil definido por un
administrador, no un dato de usuario final, y se inyecta en la lista de columnas de un
`PIVOT`. La explotabilidad real es baja. Lo que falla no es la gravedad, es que el motor
diga `0` donde debería decir `1 con matices`.

---

## 7. Plan de tareas — ordenado por dependencia

Los dos órdenes de `docs/auditoria-plantilla.md` §3. En esta auditoría **no se contradicen**,
porque ninguna tarea toca una columna derivada: todas son cambios de estructura de control
dentro de un objeto. Se dice explícitamente en vez de omitirlo.

| # | Tarea | Precedencia | Por qué orden |
|---|---|---|---|
| 1 | `dbo.DeleteEventLog`: decidir si `@LogGUID` nulo debe vaciar la tabla. Si no, `RAISERROR`; si sí, renombrar a algo que lo declare | ninguna | — |
| 2 | Envolver en `TRY/CATCH` los 4 objetos con transacción sin protección | ninguna entre sí | ejecución (son 4 objetos independientes) |
| 3 | Revisar `dbo.GetUsersBasicSearch` y `dbo.GetUsersAdvancedSearch`: parametrizar `@PropertyName`/`@PropertyValue` con `sp_executesql` como ya hace `GetAvailableUsersForIndex` | **antes que 4** | ejecución: mientras el SQL sea dinámico opaco, el grafo no puede verificar el resultado de 4 |
| 4 | Reevaluar las 18 "tablas huérfanas" | **después de 3** | ejecución: hasta que 3 no reduzca el SQL dinámico, `vw_Profile` seguirá apareciendo huérfana en falso (§2.3) |

**La dependencia 3 → 4 es el ejemplo de por qué el orden sale del grafo y no de la
severidad.** Por severidad, "revisar tablas sin uso" (limpieza, `low`) iría antes que
"parametrizar SQL dinámico" (`high`, más caro). Hacerlo en ese orden borraría una vista en
uso. La precedencia no es un juicio: es una consecuencia medida del punto ciego.

Los 3 ciclos de `CALLS` (§3) **no bloquean** ninguna de estas tareas: son auto-recursiones
dentro de un objeto, no dependencias entre objetos distintos.

### Backlog del motor, derivado de §6

| Tarea | Origen |
|---|---|
| Distinguir "sin `WHERE`" de "sin arista de filtro": marcar el paso con un `has_where` propio | §6.1 |
| Arista `Variable → Variable` para propagar taint, y ampliar la regla `crit` a la clausura transitiva | §6.2 |
| Añadir estos dos casos como controles negativos de `eval/bad-practices` antes de tocar ninguna regla | ambas |

---

## 8. Anexo — reproducibilidad

Mismo store y mismas reglas dan el mismo informe. Sin esto, la ventaja de un motor
determinista sobre un modelo redactando de cero no se puede comprobar.

```bash
# 1. Grafo + store SQLite del corpus congelado (no necesita SQL Server)
dotnet build TSqlLineageToolkit.slnx -c Release
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll \
    eval/column-recall/dnn-corpus.json out/dnn.json --columns --sqlite

# 2. audit_report.json (hotspots, huérfanas, cobertura de linaje)
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll \
    eval/column-recall/dnn-corpus.json out/dnn2.json --columns --nodestore --verify-audit

# 3. Referencias ciegas de columna
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll blind-refs dnn out/ciegas.csv

# 4. Las secciones 1, 2, 4 y 5 salen del MCP sobre out/dnn.db
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll mcp --store out/dnn.db
```

Herramientas MCP usadas, y para qué sección:

| Herramienta | Sección |
|---|---|
| `store_info` | §1 portada y frescura |
| `blind_spots` | §2.1 |
| `risks` (con `severity`) | §5 |
| `evidence` | §5, §6.1 — el `WHILE#37` de `PurgeScheduleHistory` |
| `impact` | §7 precedencias |

**Lo que no salió del MCP**: los reparto por `resolution` (§3), la detección de ciclos (§3) y
la reproducción regla a regla de los 11 `high` (§5) se consultaron directamente contra el
SQLite. Son consultas de agregación sobre todo el grafo, y el MCP está diseñado para
respuestas pequeñas (presupuesto de 2 KB), no para agregados. Es una limitación del
transporte, no del store, y se dice para que nadie intente reproducir §3 con `risks`.
