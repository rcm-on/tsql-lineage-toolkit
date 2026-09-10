---
title: Validación externa con agente de contexto corto
description: Protocolo iterativo para validar el motor contra una base de datos que no puede salir de su red, con un modelo que no es Claude, y devolver paquetes de defecto anonimizados e integrables.
read_when: Vas a ejecutar el motor contra una base corporativa ajena al corpus, o vas a integrar los paquetes que vuelven de ella.
related: [AGENTS.md, docs/VERIFICACION.md, docs/corpus-multibase.md]
stability: durable
updated: 2026-09-10
---

# Validación externa con agente de contexto corto

Escenario: el motor se ejecuta contra una base corporativa a la que no tenemos acceso, con
un agente que no es Claude (Codex, GPT en Azure Foundry) y con menos contexto del que
consume este repo. La base no puede salir de esa red; el diagnóstico sí, si va anonimizado.

Este documento es la herramienta: la **Parte A** es el prompt que se pega en ese agente,
diseñado para ejecutarse N veces sin recordar nada entre ejecuciones. Las partes B a D son
el contrato de lo que devuelve y cómo se integra aquí.

Tres invariantes que gobiernan todo lo demás:

- **Cero culpable.** Un resultado vacío es instrumento roto hasta que se demuestre lo
  contrario. `recall` que devuelve 0 ciegas sobre una base infernal es un fallo de
  ejecución, no una victoria.
- **Reducir antes de anonimizar.** Lo que se anonimiza bien es lo que ya es mínimo. Un
  procedimiento de 900 líneas con los nombres cambiados sigue siendo lógica de negocio.
- **Un defecto por iteración.** El agente cierra el paquete, olvida y vuelve a empezar.
  Ese olvido es lo que mantiene el contexto pequeño.

## Puesta en marcha en otro equipo

Lo que hace falta en la máquina donde vive la base:

- **.NET SDK 10** (`dotnet --version`). Es lo único imprescindible; el motor no instala nada
  en el servidor ni necesita permisos de administrador.
- **El repositorio**, en cualquier carpeta. Si no hay acceso a GitHub desde esa red, vale un
  zip: no hay submódulos ni descargas en tiempo de build salvo los paquetes NuGet.
- **Acceso de solo lectura a la base**, con `VIEW DEFINITION`. Lo exige
  `sys.dm_sql_referenced_entities`; sin ese permiso el catálogo devuelve menos filas y el
  recall sale inflado — mide bien y engaña, que es el peor fallo posible aquí.
- **Nada de escritura**: ningún subcomando de este protocolo crea objetos ni ejecuta
  procedimientos. La excepción es `capture-plans`, que no forma parte del ciclo normal.

Arranque, en orden, desde la raíz del repo:

```bash
dotnet build ParserGeneral.sln -c Release
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=LiveSql"

# 1. Cuanto ve el motor, y que NO ve, sobre tu base
dotnet run --project src/TSqlParser -c Release -- recall <BASE> --server <SERVIDOR> --out ciegas.csv

# 2. Reconciliacion modulo a modulo: lo que recall no puede cubrir
dotnet run --project src/TSqlParser -c Release -- coverage <BASE> --server <SERVIDOR> --out reconciliacion.csv

# 3. Que construcciones no supo tratar el recorrido (salida ANONIMA por construccion)
dotnet run --project src/TSqlParser -c Release -- extract <BASE> input.json --server <SERVIDOR>
dotnet run --project src/TSqlParser -c Release -- syntax-coverage input.json --out cobertura.csv
```

Autenticación: por defecto se usa la de Windows. Para usuario y contraseña, las variables
que lee `SqlConnections.FromEnvironment()` (ver `src/TSqlParser/SqlConnections.cs`) — nunca
credenciales en la línea de comandos, que acaban en el historial del shell.

Dos trampas del entorno que cuestan una tarde si no se saben:

- **Siempre `-c Release`.** Smart App Control bloquea el DLL de Debug recién compilado con un
  `FileLoadException 0x800711C7`. No es un fallo del código.
- **`dotnet test` antes de medir.** Si la suite no está verde, el instrumento está roto y
  cualquier cifra que salga después no significa nada.

De lo anterior, lo único que necesita conexión a la base es `recall` y `coverage`. El paso 3
funciona con un `input.json` ya extraído, así que puede ejecutarse en cualquier máquina.

## Parte A — El prompt

Se pega tal cual, una vez por iteración. Sustituir `<SERVIDOR>`, `<BASE>` y `<RUTA_REPO>`
la primera vez y dejarlo fijo en las siguientes.

```text
Eres un agente de ingeniería trabajando en el repositorio tsql-lineage-toolkit en
<RUTA_REPO>. Motor determinista de lineage T-SQL en .NET. Tu tarea es medir qué NO ve el
motor sobre la base <BASE> en <SERVIDOR>, y convertir cada hueco en un paquete de defecto
anonimizado que otro equipo pueda arreglar sin acceso a esta base.

PRESUPUESTO DE CONTEXTO (obligatorio, es la parte más importante del encargo)
- Lee como máximo 6 ficheros por iteración y como máximo 400 líneas en total.
- Ficheros permitidos: AGENTS.md, docs/prompt-validacion-externa.md, docs/VERIFICACION.md,
  y los ficheros de código que el propio síntoma señale.
- PROHIBIDO leer entero: src/TSqlParser/Program.cs, docs/plan-arquitectura.md, cualquier
  cosa bajo out/, cualquier grafo .json, cualquier .sql de más de 200 líneas.
  De esos, lee solo rangos concretos (sed -n / Select-String con contexto).
- PROHIBIDO volcar salidas largas al razonamiento. Todo comando escribe a fichero con
  --out y tú lees solo las primeras 30 filas o un recuento agrupado.
- Trabajas UN defecto por iteración. Al cerrar el paquete, la iteración termina: no
  encadenes un segundo defecto aunque te sobre contexto.

FASE 0 — Instrumento e inventario (solo si notes/validacion-externa/estado.md no existe)
1. dotnet build ParserGeneral.sln -c Release
2. dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=LiveSql"
   Si no está verde, para y repórtalo: el instrumento está roto, no midas con él.
3. Inventario real de la base, contra el catálogo, no contra el motor:
   SELECT type_desc, COUNT(*) FROM sys.objects WHERE is_ms_shipped=0 GROUP BY type_desc;
   Y aparte: módulos cifrados (OBJECTPROPERTY(object_id,'IsEncrypted')=1), ensamblados CLR,
   referencias a servidores vinculados (sys.servers) y a otras bases.
4. dotnet run --project src/TSqlParser -c Release -- recall <BASE> --server <SERVIDOR> \
     --out notes/validacion-externa/ciegas.csv
5. Escribe notes/validacion-externa/estado.md con: fecha, cifras del inventario, recall
   obtenido, nº de ciegas, y la cola de cúmulos pendientes (Fase 1).
   REGLA: si ciegas = 0 o el nº de módulos extraídos es muy inferior al del inventario,
   NO concluyas que el motor lo ve todo. Diagnostica la ejecución primero (permisos,
   base equivocada, módulos cifrados, timeout) y déjalo escrito en estado.md.
6. RECONCILIACIÓN MÓDULO A MÓDULO (obligatoria: `recall` no la cubre, ver más abajo).
   dotnet run --project src/TSqlParser -c Release -- coverage <BASE> --server <SERVIDOR> \
     --out notes/validacion-externa/reconciliacion.csv
   Toda fila con estado distinto de `ok` entra en la cola de estado.md ANTES que cualquier
   cúmulo de ciegas: un módulo que el motor no ve entero pesa más que una columna que se le
   escapa. Prioridad dentro de la cola: NO_EXTRAIDO, ERROR_PARSEO, DEFECTO_SIN_ARISTAS.
7. COBERTURA SINTÁCTICA — lo que el motor no supo tratar, dicho por él mismo:
   dotnet run --project src/TSqlParser -c Release -- extract <BASE> notes/validacion-externa/input.json --server <SERVIDOR>
   dotnet run --project src/TSqlParser -c Release -- syntax-coverage notes/validacion-externa/input.json \
     --out notes/validacion-externa/syntax-coverage.csv
   La salida agregada (tipo de nodo + cuántas veces + en cuántos módulos) es ANÓNIMA por
   construcción: no lleva nombres, ni SQL, ni estructura. Cópiala entera en estado.md y
   mándala tal cual — para arreglar un tipo de nodo sin caso no hace falta ver tu código.
   Prioriza por número de módulos afectados, no por apariciones: 40 veces en un módulo es
   un procedimiento raro; 40 veces en 8 módulos es una familia entera de lineage perdido.
8. FANTASMAS: si `recall` escribió un fichero `-fantasmas.csv`, esas son referencias que el
   motor AFIRMA y el catálogo no declara — candidatas a interpretación errónea. Las que
   vienen de dinámico resuelto ya están descontadas por el propio subcomando.

FASE 1 — Clasificar (solo si la cola de estado.md está vacía)
Agrupa ciegas.csv por causa aparente, no por módulo. Para cada grupo apunta: etiqueta,
nº de referencias, nº de módulos distintos, y un módulo representativo. Ordena por nº de
referencias descendente y escribe esa cola en estado.md. Etiquetas de partida (añade las
que hagan falta, no fuerces una que no encaje):
  sql-dinamico, tabla-temporal, cursor, merge-output, tvf-multisentencia, pivot-unpivot,
  cross-apply, sinonimo, cross-database, servidor-vinculado, modulo-cifrado,
  select-estrella, alias-ambiguo, error-de-parseo, otro

FASE 2 — Un defecto (el resto de iteraciones)
Toma el primer cúmulo de la cola de estado.md.
1. Localiza el módulo representativo y quédate SOLO con el fragmento que produce la
   ceguera. Reduce por bisección: borra mitad, comprueba que el síntoma sigue, repite.
   Objetivo: menos de 40 líneas. Si no baja de 100, dilo en la ficha y sigue.
2. ANONIMIZA según la Parte C de docs/prompt-validacion-externa.md. Es una gramática
   cerrada: cada identificador pasa a s1/t1/v1/p1/f1/c1/@a1/#tmp1/x1, cada literal a 'a',
   1 o '2000-01-01', y no sobrevive ni un comentario. El mapa nombre real -> nombre
   neutro va a notes/validacion-externa/mapa.local.csv, que NO SALE DE AQUÍ.
3. VERIFICA que el fragmento anonimizado sigue reproduciendo el síntoma:
     dotnet run --project src/TSqlParser -c Release -- from-sql db1 <paq>/input.json <paq>/repro.sql
     dotnet run --project src/TSqlParser -c Release -- <paq>/input.json <paq>/tmp-grafo.json --columns
   y comprueba que la arista esperada NO está. Si al anonimizar deja de reproducir, la
   reducción o el renombrado se comieron la causa: vuelve al paso 1.
   El grafo se queda aquí: lo que viaja es delta.md, la tabla esperado-vs-obtenido de la
   Parte B. Borra tmp-grafo.json antes de cerrar el paquete.
4. VERIFICA que no queda nada identificable: pasa el barrido de la Parte C sobre repro.sql
   y sobre la ficha. Cualquier acento, arroba, dominio, o identificador fuera de la
   gramática es un fallo bloqueante.
5. PRUEBA DEL FORASTERO (Parte C-ter). Quien reciba esto no ha visto esta base, no la verá
   nunca y no conoce este trabajo. Pregúntate por escrito: con este paquete y el repo
   delante, y nada más, ¿podría reproducir el síntoma y localizar la causa? Si no, AÑADE
   lo que falte DE LA LISTA QUE SÍ PUEDE VIAJAR — entorno (versión, compat_level,
   intercalación, QUOTED_IDENTIFIER del módulo), error literal de ScriptDom con posición
   relativa al repro, la forma sintáctica completa, cómo se concatena el dinámico,
   magnitudes redondeadas, commit del toolkit. NUNCA se resuelve aflojando la
   anonimización. Un paquete que no pasa esta prueba obliga a un viaje de ida y vuelta
   entero: cuesta más que los cinco minutos de completarlo ahora.
6. Escribe el paquete con la estructura de la Parte B. El mapa de nombres es LOCAL a este
   paquete: no reutilices los neutros de paquetes anteriores. Si el defecto necesita dos
   módulos que se llaman entre sí, van los dos en ESTE paquete, no en dos.
6. OPCIONAL — arreglo local: si la causa es evidente y cabe en un cambio pequeño en el
   extractor, hazlo, añade la prueba xUnit que lo gatea (nunca un script suelto) y vuelve
   a pasar los dos comandos de la Fase 0. Si el arreglo se te va de las manos, no insistas:
   el paquete ya vale por sí solo. Deja constancia en la ficha de lo que intentaste.
7. Actualiza estado.md: quita el cúmulo de la cola, anota el resultado en una línea.
   Si hubo arreglo, RE-MIDE (`recall` otra vez) y re-clasifica: el reparto cambia con cada
   arreglo y la cola vieja deja de ser válida.

REGLAS DE CIERRE
- No inventes lineage esperado: lo esperado se justifica contra
  sys.dm_sql_referenced_entities de esa misma base, o no se afirma.
- No toques la base: solo lectura. Nada de crear objetos ni ejecutar procedimientos.
- No escribas fuera de notes/validacion-externa/ salvo que estés en el paso 6.
- Si terminas sin paquete, di por qué en una línea en estado.md. Una iteración estéril
  documentada vale más que una ficha inventada.
```

## Reconciliación módulo a módulo — por qué `recall` no la cubre

`recall` compara **referencias de columna**: el script del catálogo filtra por
`referenced_minor_id > 0` (`eval/column-recall/extract-catalog.sql`) y `ciegas.csv` es una
lista `module,column`. Eso deja cinco huecos, todos a nivel de módulo, y todos silenciosos:

| Hueco | Por qué no aparece en `ciegas.csv` |
|---|---|
| Procedimiento que no parsea | `CatalogRecall` cuenta `ParseErrors` como **número agregado**, sin nombres. Sus referencias sí salen como ciegas, pero indistinguibles de un fallo de resolución |
| Procedimiento sin referencias de columna | Un orquestador de `EXEC`, o que solo toca tablas enteras, aporta **cero filas** al catálogo: recall no lo mide ni para bien ni para mal |
| Módulo cifrado | `sys.sql_modules.definition` es NULL → no se extrae; y `dm_sql_referenced_entities` tampoco devuelve nada → invisible en los dos lados |
| Módulo cuyas dependencias SQL Server no resuelve | El `TRY/CATCH` del cursor lo descarta y solo lo suma a un `PRINT` que `CatalogRecall` **ni siquiera lee** (usa un `SqlDataReader`) |
| CLR, extendidos, tipos fuera de `('P','V','FN','IF','TF','TR')` | No entran en la consulta de extracción ni en la del catálogo |

Los cinco los cubre el subcomando `coverage`, que cruza el inventario de `sys.objects` —CLR y
extendidos incluidos a propósito— contra los módulos realmente presentes en el `input.json`
y contra lo que el grafo afirma de cada uno:

```bash
dotnet run --project src/TSqlParser -c Release -- coverage <BASE> --server <SERVIDOR> --out reconciliacion.csv
```

Sale un CSV `module,type,definition,state,lineage_edges,uncovered_nodes,detail` y un resumen
por estado. Las consultas de abajo son la versión a mano, por si hay que ejecutarlo con un
binario antiguo o comprobar el subcomando contra algo independiente.

```sql
-- inventario.psv — lado catálogo. Incluye CLR y extendidos a propósito: deben aparecer
-- como no parseables por diseño, no desaparecer del recuento.
SET NOCOUNT ON;
SELECT LOWER(SCHEMA_NAME(o.schema_id) + '.' + o.name) + '|' + o.type_desc + '|' +
       CASE WHEN OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1 THEN 'cifrado'
            WHEN m.object_id IS NULL THEN 'sin_definicion'
            ELSE 'ok' END
FROM sys.objects o
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('P','V','FN','IF','TF','TR','PC','FS','FT','AF','X')
ORDER BY 1;
```

```powershell
# Lado motor: los módulos que el extractor sí trajo, y cuáles produjeron aristas.
$inv   = Get-Content inventario.psv | Where-Object { $_ -match '\|' } |
         ForEach-Object { $p = $_.Split('|'); [pscustomobject]@{ modulo=$p[0].Trim(); tipo=$p[1]; definicion=$p[2].Trim() } }
$extra = (Get-Content input.json -Raw | ConvertFrom-Json) |
         ForEach-Object { ($_.Name -split '::')[-1].ToLower() }
$conAristas = (Get-Content grafo.json -Raw | ConvertFrom-Json).Edges |
              ForEach-Object { $_.From } | Sort-Object -Unique

$inv | ForEach-Object {
  $estado =
    if ($_.definicion -ne 'ok')          { 'no_parseable_por_diseño' }   # cifrado, CLR, extendido
    elseif ($extra -notcontains $_.modulo) { 'NO_EXTRAIDO' }             # permisos, filtro, fallo de extracción
    elseif (-not ($conAristas -match [regex]::Escape($_.modulo))) { 'SIN_ARISTAS' }
    else { 'ok' }
  [pscustomobject]@{ modulo=$_.modulo; tipo=$_.tipo; estado=$estado }
} | Export-Csv reconciliacion.csv -NoTypeInformation -Encoding utf8
```

El cruce contra el grafo es por coincidencia de nombre dentro del id de nodo, así que es
aproximado: puede dar por bueno un módulo cuyo nombre sea prefijo de otro. Sirve para
priorizar, no para publicar una cifra. Si el número acaba importando, se hace en C# como
subcomando (`coverage`) y se gatea con una prueba, que es donde debería vivir.

Cómo se leen los tres estados que importan:

- `NO_EXTRAIDO` — el motor nunca vio ese módulo. Casi siempre permisos (`VIEW DEFINITION`)
  o un tipo que la consulta de extracción no cubre. Es el peor caso porque no produce
  ningún síntoma: el grafo sale limpio y falto.
- `SIN_ARISTAS` — se extrajo y no generó nada. Si el cuerpo tiene DML, es **defecto**;
  si es un envoltorio de `PRINT` o de permisos, es correcto. Hay que abrirlo para decidir,
  y esa decisión es exactamente el veredicto del barrido (Parte A-bis).
- `no_parseable_por_diseño` — no es un defecto, pero **sí es cobertura perdida** y va
  declarado en el informe: un cifrado que orquesta media base es un agujero real aunque
  ninguna herramienta pueda leerlo.

## Parte A-bis — Modo barrido: procedimiento a procedimiento

La Parte A persigue defectos: va del hueco al repro. El barrido hace lo contrario — recorre
el catálogo entero, módulo a módulo, y deja una fila por cada uno. Sirve para lo que las
ciegas no cuentan: procedimientos que el motor extrae **sin error y sin aristas**, parseos
que se caen en silencio, familias enteras de código que nadie miró. Es más caro, así que va
por lotes y solo abre carpeta cuando encuentra algo.

Se ejecuta con este prompt, también una vez por lote:

```text
Continúa el trabajo de docs/prompt-validacion-externa.md, MODO BARRIDO. Mismo presupuesto
de contexto de la Parte A: 6 ficheros, 400 líneas, nada de volcados largos.

LOTE
1. Abre notes/validacion-externa/barrido/inventario.csv. Si no existe, créalo con la
   cabecera de la Parte B y una fila por módulo de la base (sys.objects, is_ms_shipped=0),
   con estado=pendiente y el resto de campos vacíos.
2. Toma los 20 primeros módulos con estado=pendiente, ordenados por nº de ciegas
   descendente (de ciegas.csv) y, a igualdad, por líneas descendente.
3. Para cada uno de los 20, y sin leer el cuerpo entero si pasa de 200 líneas:
   a. Extrae y analiza solo ese módulo:
      dotnet run --project src/TSqlParser -c Release -- extract <BASE> notes/validacion-externa/barrido/tmp.json \
        --server <SERVIDOR> --object <esquema>.<nombre>
      dotnet run --project src/TSqlParser -c Release -- <...>/tmp.json <...>/tmp-grafo.json --columns
   b. Rellena su fila: lineas, parseo (ok|error|parcial), n_sentencias, n_aristas,
      n_ciegas, categorias (las etiquetas de la Fase 1 que apliquen), veredicto.
   c. VEREDICTO, y esta es la parte que importa:
        limpio     -> coherente con sys.dm_sql_referenced_entities Y pasa los controles
                      de la Parte F (invariantes e inventario léxico). Tener aristas NO
                      basta para ser limpio: un grafo plausible y falso es lo que buscamos
        sospechoso -> menos aristas que las que el catálogo declara, o falla un invariante,
                      o el cuerpo trae MERGE/PIVOT/APPLY/OPENJSON/cursor/dinámico y el
                      grafo no refleja nada de eso
        fantasma   -> el grafo afirma aristas que el catálogo no declara y que NO vienen de
                      SQL dinámico resuelto: el motor se las inventó
        defecto    -> no parsea, o parsea con CERO aristas teniendo DML dentro
      Un módulo con DML y cero aristas es DEFECTO, nunca "limpio". El motor no viendo
      nada no es el motor confirmando que no hay nada.
   d. Solo si el veredicto NO es limpio: crea barrido/procs/<id_local>/ con ficha.md y
      esqueleto.sql (Parte C-bis). Si es limpio, la fila basta y no se abre carpeta.
4. Marca los 20 como hechos, actualiza el recuento de cabecera de INVENTARIO.md y termina
   la iteración. No empieces otro lote.
```

El barrido no compite con la Parte A: la alimenta. Cuando cierra un lote, los módulos con
veredicto `defecto` entran en la cola de cúmulos de `estado.md` como candidatos a paquete
completo.

## Parte C-bis — Esqueleto de un procedimiento

Un repro de la Parte A es un fragmento de menos de 40 líneas. Un módulo del barrido es un
procedimiento entero, y entero no puede viajar aunque se renombre: la **estructura** de un
procedimiento de negocio ya es información de negocio.

Lo que viaja es el esqueleto: se conserva la forma sintáctica, se tira el contenido.

- **Se conserva:** el orden y tipo de las sentencias, los `JOIN` y su tipo, las tablas y
  columnas implicadas (ya renombradas), los `EXEC`/SQL dinámico y cómo se construye la
  cadena, `MERGE`/`OUTPUT`/`APPLY`/`PIVOT`/cursores/temporales, el flujo de control.
- **Se tira:** toda expresión que no decida lineage. Condiciones de `WHERE` → `1=1`.
  `CASE` de negocio → `NULL`. Aritmética → `1`. Cadenas → `'a'`. Cuerpos de `IF` que no
  tocan datos → `BEGIN SET @a1 = 1; END`.
- **Regla de corte:** si al quitar una expresión el grafo esperado no cambia, esa expresión
  sobra. Si cambia, se conserva pero renombrada — es parte del defecto.

El esqueleto también se somete al barrido de la Parte C, y también se verifica que sigue
reproduciendo lo que la ficha afirma. Un esqueleto que ya no reproduce es ruido: se marca
`reproduce: no` y se dice por qué, no se maquilla.

## Parte B — Estructura de lo que vuelve

Todo cuelga de `notes/validacion-externa/` (ignorado por git, así que el material de
trabajo no se publica por accidente). Lo que viaja son los `paquetes/`; lo demás se queda.

```
notes/validacion-externa/
  estado.md            estado del bucle: métricas, cola pendiente, historial de iteraciones
  ciegas.csv           salida cruda de `recall` (module,column). NO VIAJA
  mapa.local.csv       real -> neutro. NO VIAJA NUNCA, bajo ninguna circunstancia
  paquetes/
    INDICE.md          una fila por paquete: id, categoría, frecuencia, estado
    0001-sql-dinamico-exec-concatenado/
      ficha.md         frontmatter + síntoma, esperado, observado, hipótesis
      repro.sql        fragmento anonimizado, mínimo y reproducible
      delta.md         tabla esperado vs. obtenido, en texto. OBLIGATORIO
      comandos.md      comandos exactos ejecutados y su salida resumida
      observado.json   OPCIONAL: solo si el repro no reproduce fuera de esa máquina
  barrido/
    INVENTARIO.md      recuento por tipo, por veredicto y por categoría, en una tabla
    inventario.csv     una fila por módulo de la base (ver cabecera abajo)
    procs/
      p0142/           solo módulos con veredicto sospechoso o defecto
        ficha.md
        esqueleto.sql
```

Cabecera de `inventario.csv` — nombres fijos, es lo que se agrega al integrar:

```csv
id_local,tipo,lineas,parseo,n_sentencias,n_aristas,n_ciegas,categorias,veredicto,estado,paquete
```

`id_local` es el nombre neutro (`p0142`), nunca el real; su correspondencia vive en
`mapa.local.csv`. `categorias` va entre comillas y separado por `;`. `paquete` apunta al
directorio de `paquetes/` si ese módulo acabó generando uno, o queda vacío.

`ficha.md` lleva este frontmatter, con estos nombres de campo, para poder agregarlo sin
leerlo a mano:

```yaml
---
id: "0001"
titulo: EXEC con SQL concatenado en variable no resuelve la tabla destino
categoria: sql-dinamico
severidad: alta          # alta | media | baja
frecuencia: 143          # referencias ciegas de este cúmulo
modulos_afectados: 31
reproduce: si            # el repro anonimizado reproduce el síntoma
comando_repro: "from-sql db1 input.json repro.sql && TSqlParser input.json grafo.json --columns"
sospecha_codigo: "src/TSqlParser/DynamicSql.cs:120-180"   # o: desconocido
arreglo_intentado: no    # no | parcial | si
anonimizado: si
barrido_pii: limpio
prueba_del_forastero: si # ver Parte C-ter
# Entorno — no es opcional: la mitad de los defectos de parseo dependen de esto
sql_server: "Microsoft SQL Server 2016 (SP2) 13.0.5026.0, Standard Edition"
compat_level: 100
collation: "Modern_Spanish_CI_AS"
quoted_identifier: OFF   # el del módulo (sys.sql_modules.uses_quoted_identifier)
ansi_nulls: ON
toolkit_commit: e9c7e35
---
```

Y debajo, cuatro apartados de tres líneas cada uno: **Síntoma**, **Esperado**,
**Observado**, **Hipótesis**. Nada más. La ficha no es un informe: es lo justo para que
alguien con el repo delante y sin la base pueda atacar el defecto.

### Por qué el JSON no viaja

El paquete tiene que ser suficiente **sin ningún artefacto binario ni volcado de grafo**, y
lo es por una razón simple: quien lo recibe tiene `repro.sql` y tiene el motor, así que
**regenera el grafo él mismo**. Mandar `observado.json` es mandar algo que el receptor puede
producir en diez segundos — y que además es voluminoso, difícil de revisar y el sitio más
fácil por donde se cuela un nombre real olvidado en una propiedad de nodo.

Lo que sí hace falta es el **delta en texto**, `delta.md`, que es la afirmación que el JSON
no hace por sí solo:

```markdown
| # | Arista esperada | ¿Está? | Qué salió en su lugar |
|---|---|---|---|
| 1 | p1 --lee--> s1.t1.c1 | no | nada |
| 2 | p1 --escribe--> s1.t2.c3 | no | p1 --escribe--> #tmp1.c3 |
| 3 | p1 --lee--> s1.t3.c2 | sí | — |

Esperado según: sys.dm_sql_referenced_entities sobre el módulo original (3 refs de columna).
Obtenido con: toolkit e9c7e35, `from-sql db1 input.json repro.sql` + `--columns`.
```

Con eso y el repro se reproduce, se diagnostica y se escribe la prueba. El JSON solo viaja
en un caso: cuando el repro **no** reproduce fuera de esa máquina y hay que comparar dos
grafos para entender por qué. Es la excepción, y va justificada en la ficha.

## Parte C — Anonimización

Es una **gramática cerrada**, no una lista de cosas que borrar. El repro solo puede
contener palabras clave de T-SQL más este vocabulario:

| Elemento | Forma | Ejemplo |
|---|---|---|
| Base de datos | `db1`, `db2` | `db2.s1.t1` |
| Servidor vinculado | `srv1` | `srv1.db1.s1.t1` |
| Esquema | `s1`, `s2` | `s1.t1` |
| Tabla | `t1`, `t2` | |
| Vista | `v1` | |
| Procedimiento | `p1` | |
| Función | `f1` | |
| Columna | `c1`, `c2` | |
| Parámetro o variable | `@a1` | |
| Temporal / variable de tabla | `#tmp1`, `@vt1` | |
| Alias | `x1`, `x2` | |
| Cadena | `'a'` | |
| Número | `1` | |
| Fecha | `'2000-01-01'` | |

Comentarios: se borran todos, sin excepción — son el escondite habitual de nombres de
cliente, tickets y correos. Si un comentario era necesario para entender el defecto, eso
va en la ficha, no en el `.sql`.

Barrido de comprobación, en PowerShell, sobre el paquete entero. Debe salir vacío:

```powershell
$paq = "notes\validacion-externa\paquetes\0001-...\"
# 1. Red de seguridad: nada identificable, en ningún fichero del paquete
Select-String -Path "$paq\*" -Pattern '[áéíóúñÁÉÍÓÚÑ]|@\w+\.\w+|https?://|\bES\d{2}\b|\b\d{8}[A-Za-z]\b'
# 2. Gramática cerrada: identificadores del repro fuera del vocabulario permitido
(Select-String -Path "$paq\repro.sql" -Pattern '[A-Za-z_][A-Za-z0-9_]*' -AllMatches).Matches.Value |
  Sort-Object -Unique |
  Where-Object { $_ -notmatch '^(db|srv|s|t|v|p|f|c|a|x|vt|tmp)\d+$' }
```

La segunda lista no sale vacía: contendrá las palabras clave de T-SQL (`SELECT`, `FROM`,
`EXEC`, tipos...). Se revisa **a ojo, entera**, y toda palabra que no sea T-SQL puro es una
fuga. Es el paso que no se puede automatizar del todo y el que hay que hacer despacio.

Regla de última instancia: ante la duda sobre un fragmento, no viaja. La ficha puede
describir en prosa neutra un patrón que no se puede enseñar; media pista es mejor que una
filtración.

## Parte C-cuater — El circuito que no necesita anonimizar

Todo lo anterior gestiona un riesgo: SQL que sale de una red ajena, reducido y renombrado a
mano, con un barrido de PII y una revisión que no se puede automatizar del todo. Hay una
clase de defecto —los de **parseo puro**— que no necesita nada de eso, porque el diagnóstico
no puede contener datos:

```bash
dotnet run --project src/TSqlParser -c Release -- syntax-coverage <input.json> --anon --out diag.json
```

El fichero lleva exactamente cuatro cosas: el identificador del esquema, la fecha, recuentos,
y nombres de **tipos de nodo de ScriptDom** (`VariableTableReference`, `MergeStatement`...)
más **números de error** del parser. Ni un nombre, ni una línea de SQL, ni la forma del
modelo. No se anonimiza: es que no hay nada que anonimizar.

### Cómo lo compruebas tú, sin fiarte del programa

El subcomando imprime el **contenido íntegro** del fichero —cabe en pantalla— y luego una
comprobación que puedes repetir a mano:

> Todo texto del informe tiene que ser vocabulario cerrado: el identificador de esquema, una
> fecha, el nombre de una familia (`statement` / `table_reference`), o **un tipo que existe de
> verdad en el ensamblado de ScriptDom de Microsoft**. Un nombre de tu base no puede cumplir
> esa última condición, porque no es una clase del parser.

Si al leerlo reconoces una palabra tuya, es un fallo y el fichero no sale. Y si la
comprobación no pasa, el subcomando devuelve código 1 y no te deja seguir por inercia.
Está gateado en las dos direcciones (`VerifyAnonymous_CazaUnTextoQueNoSeaTipoDeScriptDom`):
un gate que no puede ponerse rojo no protege de nada.

### Qué se puede arreglar solo con eso

Un tipo de nodo sin caso y su número de apariciones bastan para escribir el arreglo **y su
prueba**, porque el caso mínimo lo redactamos aquí: para `VariableTableReference` no hace
falta tu procedimiento, basta con `SELECT c1 FROM @tv v`. Ese fue literalmente el camino de
los dos huecos del 2026-09-10.

Lo que NO se resuelve así: lineage mal atribuido, resolución de alias, dinámico. Para eso
sigue haciendo falta el paquete con repro de la Parte A. La regla práctica: **primero manda
el `--anon`**, que es gratis y no arriesga nada; y solo monta un paquete completo para lo que
quede sin explicar después.

## Parte C-ter — El contrato de lo que sobrevive

Quien recibe el paquete no ha visto esa base, no la verá nunca y no tiene el contexto de
ese trabajo. Un paquete anonimizado de más es tan inútil como una filtración es
inaceptable: la anonimización que borra el defecto no protege nada, solo hace perder dos
viajes. Este es el reparto, y **no es negociable en ninguna de las dos direcciones**.

### Sobrevive siempre — y es obligatorio, no opcional

Nada de esto es información de negocio; todo es necesario para arreglar un parser:

- **La forma sintáctica completa.** Orden y tipo de sentencias, tipo de cada `JOIN`, `CTE`,
  `APPLY`, `MERGE`/`OUTPUT`, cursores, temporales, `PIVOT`, flujo de control. Es *cómo está
  escrito*, no *qué hace*.
- **Los tipos de datos.** `nvarchar(max)` frente a `varchar(50)` cambia la resolución. Se
  conserva el tipo; la longitud puede redondearse a un tramo (`varchar(50)` → `varchar(50)`
  está bien; solo se generaliza si la longitud fuese un dato de negocio, que casi nunca lo es).
- **Cómo se compone el SQL dinámico.** Qué se concatena, en qué orden, qué viene de
  variable, si pasa por `sp_executesql` con parámetros o por `EXEC(@s)`. Con nombres
  neutros, esto es puramente estructural — y es la causa raíz más frecuente.
- **El entorno.** Versión y edición de SQL Server, `compatibility_level`, intercalación,
  `QUOTED_IDENTIFIER` y `ANSI_NULLS` del módulo. Un `compat_level` 100 con `*=` explica
  media ficha, y sin ese dato el diagnóstico de aquí sale mal.
- **El error literal de ScriptDom**, si lo hubo: número, mensaje y posición **relativa al
  repro**, nunca al módulo original.
- **Las magnitudes, redondeadas.** "≈700 procedimientos, ≈40 con este patrón" orienta la
  prioridad. "703 procedimientos en la base de facturación de tal cliente" no.
- **El commit del toolkit** con el que se midió. Sin él, no se sabe contra qué código
  reproducir.

### No sale nunca

Nombres reales (objetos, esquemas, servidores, bases, personas), literales, comentarios,
volumetrías exactas, y **la topología del modelo más allá del repro**: qué tabla se une con
qué, a lo largo de muchos procedimientos, *es* el modelo de negocio aunque cada nombre esté
cambiado.

De ahí una regla que parece un error y no lo es: **el mapa de nombres es local a cada
paquete**. `t1` en el paquete 0003 no es la misma tabla que `t1` en el 0007, y no debe
serlo. Si fuese consistente entre paquetes, cualquiera podría reconstruir el esquema
completo juntándolos. Se pierde la correlación entre paquetes a propósito; ese es el precio
y merece la pena. Cuando un defecto **necesita** dos módulos que se llaman entre sí, los dos
van en el **mismo** paquete, con mapa compartido dentro de él y solo dentro de él.

### La prueba del forastero

Antes de cerrar cualquier paquete, el agente responde por escrito a esta pregunta:

> Con este paquete y el repositorio delante, sin acceso a la base y sin haber hablado
> conmigo, ¿podría alguien reproducir el síntoma y localizar la causa?

Si la respuesta es no, falta información **de la lista de arriba** — no se resuelve
aflojando la anonimización, se resuelve añadiendo lo que sí puede viajar: el entorno, el
error exacto, la forma completa de la sentencia, un segundo fragmento igual de neutro. Solo
si tras eso sigue siendo no, se marca `prueba_del_forastero: no` y se explica en una línea
qué haría falta: eso ya es una decisión tuya, no del agente.

Cuando un paquete llega aquí con `prueba_del_forastero: si` y aun así no se puede
diagnosticar, el fallo es de este contrato, no del paquete. Se anota qué faltó y se corrige
esta sección antes de la siguiente campaña.

## Parte F — Cuando el error no salta

El caso peor no es el procedimiento que revienta: es el que parsea limpio, produce aristas,
y las produce **mal**. No hay excepción, no hay ciega, no hay nada que mirar. Un grafo
plausible y falso es más dañino que un grafo vacío, porque el vacío se nota.

Contra eso no vale un solo instrumento. Estos cinco, de más barato a más caro:

### 1. Fantasmas: la mitad que nunca medimos

`recall` calcula `catálogo \ grafo` — lo que el motor no vio. La resta inversa,
`grafo \ catálogo`, son aristas que el motor se **inventó**, y hoy no se calcula en ningún
sitio. Sale de los mismos dos conjuntos que ya están en memoria en `CatalogRecall.Compute`,
así que es gratis: toda tripleta `módulo|entidad|columna` del grafo que el catálogo de SQL
Server no declara es sospechosa de misinterpretación.

Con una salvedad que hay que respetar o el número no sirve: el catálogo **no ve el SQL
dinámico resuelto**, y ahí el motor sí llega. Una arista que venga de un `Step` con
`is_dynamic_sql` resuelto no es fantasma, es exactamente el valor del producto. Se separan
los dos recuentos o se acusa al motor de su mejor función.

### 2. Invariantes del grafo — no necesitan catálogo de dependencias

Se comprueban contra `sys.columns`/`sys.objects`, que están siempre y son baratos:

- Toda columna de una arista existe en la tabla que dice. Si no existe, el motor la
  **inventó**: resolución de alias equivocada, casi siempre.
- Todo destino de escritura existe como tabla o vista.
- Un módulo con `INSERT`/`UPDATE`/`DELETE`/`MERGE` en el cuerpo tiene al menos una arista
  de escritura. Cero es defecto, nunca "es que no hace nada".
- Un `SELECT *` sobre una tabla expande **tantas columnas como `sys.columns` declara**.
  Menos columnas es expansión rota; más, alias cruzado.
- Ninguna arista invierte el sentido: lo que aparece en `FROM` no se marca escrito, lo que
  aparece tras `INTO` no se marca leído.

### 3. Doble vía: el motor contra sí mismo

El mismo módulo, extraído de la base viva (`extract --object`) y desde fichero
(`from-sql`), tiene que dar **el mismo grafo**. No hace falta oráculo externo: cualquier
divergencia es un defecto del motor, y aparece sin que nadie sepa cuál es la respuesta
correcta. Es la comprobación más barata de todas y la que menos se hace.

### 4. Cobertura sintáctica: que el motor declare lo que no entiende

Si un módulo trae un tipo de nodo de ScriptDom para el que el recorrido no tiene caso, antes
seguía adelante sin avisar y producía un grafo incompleto con aspecto de completo. Ahora lo
declara:

```bash
dotnet run --project src/TSqlParser -c Release -- syntax-coverage <input.json> --out cobertura.csv
```

El `default` del recorrido registra el tipo de nodo, y la salida agrupa por tipo: cuántas
veces y **en cuántos módulos**. Prioriza siempre por módulos, no por apariciones — 40 veces
en un módulo es un procedimiento raro; 40 veces en 8 módulos es una familia entera de
lineage perdido.

Se distingue lo benigno declarado (`SET NOCOUNT`, permisos, mantenimiento) de lo que no lo
es. Estar en la lista de benignos no es "da igual": es una afirmación revisable de que esa
construcción no mueve datos.

Este instrumento se estrenó el 2026-09-10 sobre los corpus del repo y encontró a la primera
dos huecos que ningún otro medía: `VariableTableReference` (`FROM @tv`, 40 apariciones en 8
módulos de DNN y 22 en 3 de WWI) y `JoinParenthesisTableReference` (`FROM (a JOIN b)`, que
perdía **los dos** lados). Ver la Bitácora del día para lo que costó y lo que devolvió.

### 5. Planes reales: la única verdad para el dinámico

`capture-plans` + `enrich-from-plans` contrastan contra lo que SQL Server **ejecutó de
verdad**. Es lo único que valida el SQL dinámico resuelto, que es justo donde el catálogo
no llega. Caro, requiere carga real y atribución por Extended Events (el `nest_level` del
`event_file`; Query Store devuelve `object_id = 0` y no sirve). No es para cada iteración:
es para cerrar la campaña, o para cuando un cúmulo de dinámico se resiste.

### Lo que esto demostró el primer día

Los dos huecos de arriba se arreglaron el 2026-09-10. La medida antes y después:

| Medida | Antes | Después |
|---|---|---|
| Ciegas sobre DNN (`recall`) | 22 | **22** |
| Nodos sin cubrir no benignos (DNN) | 10 filas | 1 fila |
| `SELECT c1 FROM @tv v JOIN dbo.t2 x` | afirmaba `dbo.t2.c1` | no afirma nada falso |

La primera fila es la lección: **el recall no se movió ni una referencia**, y aun así había
un defecto real y una atribución inventada. El catálogo de SQL Server no conoce las
variables de tabla, así que esas referencias nunca pudieron aparecer en su lista de ciegas.
Un instrumento no encuentra lo que no puede ver, y medir más fuerte con el mismo instrumento
no lo arregla.

### Qué hace el agente con esto

En el barrido, un módulo ya no es `limpio` solo por tener aristas: pasa los invariantes del
punto 2 y el barrido léxico del punto 4. Los fantasmas del punto 1 se listan aparte,
separando los que vienen de dinámico resuelto. Y cuando algo huele mal sin síntoma claro,
el punto 3 dice si el motor se contradice a sí mismo, que ya es defecto probado sin
necesidad de saber cuál era la respuesta buena.

## Parte D — Integración de vuelta

Lo que se trae: el directorio `paquetes/` completo. Nada más — ni `ciegas.csv`, ni
`mapa.local.csv`, ni `estado.md` (sus cifras ya están en las fichas).

Al integrarlo aquí:

1. Leer `paquetes/INDICE.md` y ordenar por `frecuencia`, no por `severidad`: el cúmulo que
   más referencias ciegas explica es el que más recall devuelve por arreglo.
2. Por cada paquete, `repro.sql` entra en el corpus de eval como caso, y `esperado.md` se
   convierte en la aserción de una prueba xUnit. El caso entra **antes** que el arreglo:
   primero rojo, luego verde. Nada de scripts sueltos de Node para gatear el motor.
3. El arreglo se verifica además contra las bases locales vivas (WWI, AdventureWorks, DNN),
   no solo contra el repro: un repro anonimizado demuestra el defecto, no la ausencia de
   regresión.
4. Devolver al trabajo la versión arreglada y **re-medir allí**: el número que cuenta es
   el `recall` sobre la base infernal, y solo se puede leer desde dentro de esa red.

## Parte G — Ensayo en local con un agente menor

Antes de que este protocolo se ejecute en una red donde no podemos ver lo que pasa, conviene
probarlo aquí con un modelo del mismo orden que el que habrá allí (Sonnet, no Opus). Lo que
se está probando **no es el motor: es el protocolo**. Si un agente competente pero sin
contexto no puede seguirlo, el problema es el documento.

El ensayo usa el corpus congelado de DNN (867 módulos) como sustituto de la base infernal, y
`blind-refs` en lugar de `recall`: hace la misma comparación, contra el catálogo `.psv` que
ya está en el repo, sin necesitar servidor. `coverage` queda fuera del ensayo por lo mismo —
requiere base viva — y eso se declara como no evaluable en vez de disimularlo.

La pieza que hace útil el ensayo: se dirige al agente a un hueco **cuya respuesta ya
conocemos** (`VariableMethodCallTableReference`, la única fila no benigna que queda en DNN).
Así el resultado se puede corregir como un examen, en vez de tener que creérselo.

El prompt está en la sección homónima de este documento; se pega tal cual, sin más contexto.

## Parte E — Prompt de ingesta, diagnóstico y plan

Este es el otro extremo del puente: se pega aquí, con los paquetes ya copiados en el repo,
para convertir el material en un plan de reparación ordenado.

```text
Tienes en <RUTA> el material que vuelve de una validación externa: paquetes/ (fichas de
defecto con repro anonimizado) y, si se ejecutó el barrido, barrido/ (inventario por
módulo y esqueletos de los sospechosos). El protocolo que los generó es
docs/prompt-validacion-externa.md — Partes B y C-bis describen el formato exacto.

ORDEN DE LECTURA (no lo alteres: es lo que evita leerlo todo)
1. paquetes/INDICE.md y barrido/inventario.csv. Agrega: reparto por categoría, por
   veredicto y por frecuencia acumulada. De aquí sale la foto, no de las fichas.
2. Solo el frontmatter de cada ficha.md. Con eso ya puedes agrupar.
3. El cuerpo de una ficha, su delta.md y su repro.sql solo cuando vayas a diagnosticar ESE
   cúmulo. No esperes un grafo: no viene, y no hace falta. Tienes el repro y el motor, así
   que el grafo lo generas tú ejecutando comando_repro. Si el paquete trae observado.json,
   es la excepción — significa que allí NO reproducía, y lo primero es averiguar por qué.

DIAGNÓSTICO
- Agrupa por causa raíz en el motor, no por categoría declarada: varias etiquetas
  distintas suelen ser el mismo defecto (p. ej. tabla-temporal y merge-output cayendo
  ambos en la resolución de destino). Di explícitamente qué fichas colapsas en una causa.
- Por cada causa: dónde vive en el código (proyecto y fichero, verificado abriéndolo, no
  de memoria), por qué falla, y qué referencias ciegas devolvería arreglarla.
- Trata las cifras como lo que son: medidas sobre OTRA base, hechas por otro agente.
  Reproduce cada repro aquí antes de creerte su veredicto. Un `reproduce: si` que no
  reproduce es el primer hallazgo del informe, no un error de transcripción.
- Lo no evaluable se declara: módulos cifrados, CLR, servidores vinculados y cualquier
  cosa que el paquete no permita comprobar van a una sección aparte, no al plan.

ENTREGABLES (dos ficheros, nada más)
1. docs/diagnostico-<base>.md — la foto: cobertura observada, reparto por causa raíz,
   qué está roto y qué solo lo parece, y lo no evaluable.
2. docs/plan-reparacion-<base>.md — una tabla de tareas, ordenadas por ciegas recuperadas
   por unidad de esfuerzo, con estas columnas:
     id | causa raíz | fichas que cierra | ficheros a tocar | gate (prueba xUnit concreta)
        | ciegas estimadas | esfuerzo (S/M/L) | depende de
   Cada tarea debe poder empezarse sin leer nada más que su fila y su ficha.

REGLAS
- El gate de cada tarea es una prueba xUnit en tests/, con el repro incorporado al corpus
  de eval. Primero rojo, luego el arreglo. Nada de scripts sueltos.
- Ninguna tarea se da por buena solo con el repro: se verifica también contra las bases
  vivas locales (WWI, AdventureWorks, DNN) para descartar regresión.
- Si un cúmulo grande no tiene arreglo razonable, dilo y propón el siguiente por tamaño.
  Un plan honesto con un hueco declarado es mejor que uno completo e inventado.
```

El ciclo completo, entonces: barrido y paquetes allí → diagnóstico y plan aquí → arreglos
con su gate → binario de vuelta → `recall` otra vez sobre la base infernal. Ese último
número, y no el corpus local, es el que dice si el motor mejoró.
