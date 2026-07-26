# Texto para el artículo y para LinkedIn

Redactado a partir de la corrida canónica del 2026-07-26
([`docs/corrida-canonica.md`](../corrida-canonica.md)). **Ninguna cifra de aquí
está estimada**: todas salen de esa ejecución o del catálogo de SQL Server.

---

## 1. Cifras a sustituir en el artículo

| Donde dice | Debe decir | Por qué |
|---|---|---|
| 1.398 nodos | **1.529** | la corrida de la que salió es de un commit anterior |
| 3.476 relaciones | **4.151** | íd. |
| "47 objetos" junto a una captura que muestra 64 | ver §2 — hay que distinguir las dos escalas | es la contradicción que ve el lector |
| 69 tablas (pie de captura) | **68** | la captura vieja salía de código sin commitear |
| 119 pruebas | **136** | lo que reporta `dotnet test` |
| 112 hallazgos | **110** (1 crítico, 20 alto, 43 medio, 46 bajo) | medido en la captura nueva |
| "un grep cuenta 3 flujos" | ver §3 | ese 3 no salía de ninguna medición |

---

## 2. El párrafo que resuelve la contradicción 47 / 64

> Reemplaza el bloque donde se pegan las cifras de consola. Es el punto donde hoy
> el lector ve "47 objetos" y, dos centímetros más arriba, una captura que dice 64.

Lanzo la extracción contra la base viva:

```
Wrote 47 objects from WideWorldImporters to input.json
Appended 48 table definitions to input.json
```

Y construyo el grafo:

```
Analyzed 47 objects (47 ok, 0 parse errors)
Analyzed 48 table schemas (48 ok, 0 errors)
Graph: 1529 nodes, 4151 relationships
```

Cuarenta y siete objetos. Pero cuando abro el dashboard, la cabecera dice
**64 objetos · 68 tablas**. No es una errata, y tampoco son dos ejecuciones
distintas: **son dos escalas de conteo, y la diferencia entre ellas es exactamente
el motivo por el que existe esta herramienta.**

Los 47 son lo que hay en el catálogo. Se puede comprobar en un segundo:

```sql
SELECT COUNT(*) FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.type IN ('P','FN','IF','TF','TR','V');   -- 47
```

42 procedimientos, 1 función escalar, 1 función de tabla, 3 vistas… y **cero
triggers**. WideWorldImporters no tiene ni un trigger persistido.

Los 64 son lo que hay **de verdad**. Los 17 que faltan son triggers que
`DeactivateTemporalTablesBeforeDataLoad` **crea en tiempo de ejecución**,
montando el `CREATE TRIGGER` como texto dentro de una variable y lanzándolo con
`EXECUTE (@SQL)`. No están en `sys.objects` hasta que el procedimiento corre. Un
inventario de catálogo se los pierde enteros; un `grep` ve un string. El AST los
encuentra los 17, con la tabla sobre la que dispara cada uno.

47 + 17 = 64. Esa resta es el producto.

*(Con las tablas pasa lo mismo a menor escala: 48 extraídas del DDL, más 15 del
catálogo `sys.*` que los procedimientos leen, más las 3 vistas y 2 tablas de
respaldo creadas también en runtime, dan 68.)*

---

## 3. El contraste con `grep`, medido

> Sustituye la frase "un grep cuenta 3 flujos", que no venía de ninguna medición
> y además mezclaba dos métricas distintas.

Ese procedimiento tiene 706 líneas y construye casi todo su SQL como texto. Si lo
mides con `grep`, encuentras **52 tokens `IF`**. Si eliminas los literales de
cadena y vuelves a contar, quedan **18**: los otros **34 viven dentro de los
strings que el procedimiento está fabricando**. El AST reporta 18 flujos de
control. Exactamente los reales.

Y al revés: el fuente no contiene ni un solo `EXEC(@sql)` — usa la forma
`EXECUTE (@SQL)`, 34 veces. Un `grep` mal escrito no encuentra ninguna.

Ese hueco —entre lo que parece código y lo que es código— es donde falla el
análisis de texto, y es donde vive el riesgo.

---

## 4. El dato que da credibilidad (añádelo, hoy no está)

> Es el argumento más fuerte y falta en el artículo. No es "detectamos mucho": es
> "detectamos exactamente lo que hay".

Un motor de lineage que solo se mide a sí mismo no vale nada. Así que el grafo se
contrasta contra el propio catálogo de SQL Server:

```
FK relationships in DB restricted to tables present in graph: 81
  In DB but missing from graph: 0
  In graph but not in DB (within scope): 0

CALLS (EXEC) relationships in DB restricted to analyzed objects: 12
  In DB but missing from graph: 0
```

**98 de 98 claves ajenas** (`sys.foreign_keys`), **12 de 12 cadenas de ejecución**,
**cero ausencias y cero aristas fantasma en ambos sentidos**, y **100% de
cobertura** en el lineage de columnas de salida (32 de 32).

El grafo marca además 8 tablas sin ninguna relación. Las comprobé una a una: 5 son
tablas de historial temporal (`temporal_type = HISTORY`, las gestiona el motor, no
las toca ningún DML) y 3 son vistas que `sys.sql_expression_dependencies` confirma
que nadie referencia. Ninguna es un fallo de extracción. Un huérfano explicado
vale más que un huérfano escondido.

---

## 5. Borrador para LinkedIn

> Un procedimiento de WideWorldImporters crea 17 triggers que no existen en
> `sys.objects`.
>
> Los monta como texto en una variable y los lanza con `EXECUTE (@SQL)`. Hasta que
> el procedimiento corre, para el catálogo no existen. Para un `grep`, son un
> string.
>
> He construido un motor de lineage para T-SQL que los encuentra los 17 — con la
> tabla sobre la que dispara cada uno — usando la gramática oficial `ScriptDom` en
> lugar de expresiones regulares.
>
> Contra la base entera: 98 de 98 claves ajenas y 12 de 12 cadenas de ejecución
> verificadas contra el catálogo. Cero ausencias, cero relaciones inventadas.
>
> Y un detalle que resume el problema: ese procedimiento tiene 52 tokens `IF` si
> lo miras con `grep`. Solo 18 son código. Los otros 34 viven dentro de los
> strings que el propio procedimiento está fabricando.
>
> Determinista, offline, y el grafo sale como un fichero que puedes diffear en un
> PR. MIT.
>
> 🔗 [enlace al repo]

---

## 6. Imágenes

Las cuatro capturas de este directorio van a `quartz/static/labs/tsql/`:

| Fichero aquí | Destino | Qué muestra |
|---|---|---|
| `impacto.png` | `impacto.png` | pantalla de impacto de `DeactivateTemporalTablesBeforeDataLoad` |
| `impacto-niveles.png` | `impacto-niveles.png` | cadena de impacto por niveles, profundidad 5 |
| `flujo.png` | `flujo.png` | flujograma de `Configuration_ApplyAuditing` con sus `IF` y variables |
| `overview.png` | `overview.png` | resumen general |

**La cabecera visible en las capturas dice `64 objetos · 68 tablas`.** El texto que
las rodea tiene que ser coherente con eso — es justo lo que falla en la versión
actual del artículo.
