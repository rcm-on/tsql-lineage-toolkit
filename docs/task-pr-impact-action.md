# Tarea: GitHub Action de comentario de PR (impact diff)

**Estado:** HECHO (Sonnet, 2026-07-05). Implementa la sección "Después" de
`docs/task-change-map-diff.md` (el diff de `change_map.json` ya estaba
cerrado, `ChangeMapDiff.cs` / subcomando `diff-change-map`).

## Qué hace `.github/workflows/pr-impact.yml`

En cada `pull_request` contra `master`:

1. Checkout del head del PR con historia completa (`fetch-depth: 0`, necesaria
   para poder calcular el merge-base más adelante).
2. `dotnet build src/TSqlParser/TSqlParser.csproj -c Release` — se compila
   **una sola vez**; el mismo `TSqlParser.dll` (versión del head del PR) se usa
   para generar ambos stores. El diff es de *corpus SQL* (antes vs. después),
   no de versión del analizador.
3. `git merge-base HEAD origin/<base-ref>` para encontrar el commit común con
   la rama base, y `git worktree add` para tener ese commit en un checkout
   separado (`$RUNNER_TEMP/pr-impact-base`) sin tocar el checkout del head.
4. Genera el store **BEFORE** corriendo `from-sql` + el pipeline principal
   (`--columns --nodestore`) sobre el corpus del worktree del merge-base, y el
   store **AFTER** sobre el mismo corpus en el checkout del head.
5. `diff-change-map before.nodes after.nodes diff.json` (sin
   `--fail-on-new-impact` — ver "Por qué report-only" abajo).
6. Sube `diff.json` completo como artefacto (`pr-impact-diff`).
7. Publica o actualiza **un único** comentario en el PR (via
   `actions/github-script`) con el resumen renderizado; el JSON completo no va
   en el comentario, solo en el artefacto.

Permisos mínimos: `contents: read`, `pull-requests: write`.

## Corpus elegido: `eval/bad-practices/sql`

De los directorios bajo `eval/` (`agent-benchmark`, `auditor-challenge`,
`bad-practices`, `community-edge-cases`, `sqlglot-oracle`, `view-lineage`),
`eval/bad-practices/sql` es el único que:

- Ya tiene un pipeline de referencia probado (`eval/bad-practices/run.ps1` /
  `run.sh`: `from-sql` → graph con `--columns` → evaluación), o sea que
  `from-sql` sobre ese directorio es un camino ya ejercitado, no uno nuevo.
- Contiene tablas + procedimientos con lineage cruzado real entre archivos
  (p. ej. `dbo.Customers` / `dbo.Orders` escritas y leídas por varios
  `usp_*`), que es justo lo que `diff-change-map` necesita para producir
  `via_data_added` / `newly_affected` no triviales.
- Es pequeño (21 archivos) y determinista — corre en segundos, sin
  dependencia de una base de datos viva (a diferencia de
  `sqlglot-oracle`/`view-lineage`, que están pensados para comparar contra
  SQL Server real).

Verificado localmente (ver sección de verificación en el mensaje de la
tarea): `from-sql PrDogfood ... eval/bad-practices/sql/*.sql` seguido de la
generación de grafo con `--nodestore` corre limpio (16 objetos analizados
correctamente, 1 error de parseo esperado y documentado —
`99_usp_Broken_ParseError.sql`, que ya es intencional en el corpus — y 5
tablas).

Nombre de base de datos usado: `PrDogfood` (estable, no colisiona con
`BadPracticesDB`, que ya usa `run.ps1`/`run.sh` para el pipeline de
evaluación de reglas).

**Nota sobre el propio dogfood:** en este repo, el "cambio" que
`diff-change-map` puede ver solo existe si un PR toca archivos dentro de
`eval/bad-practices/sql`. Para el resto de PRs (que tocan `src/TSqlParser`,
tests, docs, etc.) el diff estará vacío y el comentario dirá "no impact" — es
el comportamiento esperado del *mecanismo* corriendo sobre sí mismo, no un
bug. La utilidad real de esta Action es para un repo SQL downstream que
apunte el corpus a sus propios `.sql` (ver sección de adaptación).

## Por qué report-only (sin `--fail-on-new-impact`)

La spec de `task-change-map-diff.md` deja el gate como opcional. Se decide
NO activarlo en v1 de esta Action por dos razones:

1. El corpus de dogfood de este repo (`eval/bad-practices/sql`) es
   deliberadamente un catálogo de malas prácticas con lineage cruzado denso;
   activar el gate aquí generaría falsos "rojo" en cualquier PR que toque ese
   corpus por razones no relacionadas con impacto real (p. ej. añadir un caso
   de prueba nuevo), entrenando a la gente a ignorar el gate.
2. Es la primera vez que el comentario automático corre en un repo real; se
   prefiere que el equipo vea unas cuantas rondas de comentarios
   informativos antes de convertir el hallazgo en bloqueante.

El flag ya existe en el CLI (`diff-change-map ... --fail-on-new-impact`,
exit 2 si hay impacto nuevo) y el workflow deja un comentario YAML señalando
dónde añadirlo cuando se decida activar el gate.

## Cómo lo adapta un repo SQL downstream

1. Copiar `.github/workflows/pr-impact.yml`.
2. Cambiar `corpus="$RUNNER_TEMP/pr-impact-base/eval/bad-practices/sql"` y
   `corpus="$(pwd)/eval/bad-practices/sql"` por la ruta real de los `.sql` del
   repo (o un glob que cubra varios subdirectorios — `from-sql` acepta
   `dir` o `glob` como último argumento).
3. Cambiar `PrDogfood` por un nombre de base de datos estable del propio
   proyecto.
4. Si el repo no compila con `dotnet build src/TSqlParser/TSqlParser.csproj`
   porque no vive el toolkit en el mismo repo, sustituir ese paso por
   descargar/instalar el `TSqlParser.dll` publicado (release asset o paquete
   interno) en vez de compilarlo desde fuente.
5. Para activar el gate: añadir `--fail-on-new-impact` al paso "Diff change
   maps" y decidir si el job debe fallar (`exit 2` ya hace que el paso de
   `run:` falle solo, sin cambios extra) o solo anotarse en el resumen.
6. El `branches: [master]` de `pull_request:` debe ajustarse a la rama base
   real del repo destino (`main`, etc.).

## Decisión: comentario "no impact" en vez de skip silencioso

El workflow **siempre** publica/actualiza un comentario, incluso cuando el
diff está completamente vacío (ver `isEmpty` en el script de
`actions/github-script`). Se descarta la alternativa de "skip silencioso"
porque el mismo comentario se actualiza en cada push del PR (identificado
por el marcador oculto `<!-- pr-impact-comment:v1 -->`): si un push anterior
sí generó impacto y se publicó un comentario, y un push posterior lo revierte
o lo corrige, saltarse el comentario dejaría el aviso de impacto obsoleto
visible en el PR para siempre. Actualizarlo a "no impact" es más honesto que
desaparecer.

## Verificación realizada

Dry-run local completo (mismo orden de comandos que el workflow, DLL Release
compilado localmente) contra dos copias de `eval/bad-practices/sql`, con un
cambio sintético en `usp_LogEverything_RepeatWrites` (se le añade una
escritura nueva a `dbo.Customers`, tabla leída por otros tres objetos). El
diff resultante mostró correctamente `via_data_added` sobre `dbo.Customers`
y `newly_affected` con los tres consumidores, y el renderizado del comentario
(misma lógica que el paso `github-script`, ejecutada con `node` de forma
independiente) reprodujo el resumen esperado. También se verificó el caso de
diff vacío (mismo store contra sí mismo) para confirmar el mensaje "no
impact". Detalle completo (comandos + salidas) en el mensaje de la tarea.

Un detalle de implementación a tener en cuenta si se modifica el script: el
CLI escribe el JSON de salida en UTF-8 **con BOM**; `JSON.parse` en Node no
lo descarta solo, así que el script de `github-script` hace
`.replace(/^﻿/, '')` antes de parsear (confirmado localmente: sin ese
strip, `JSON.parse` lanza `Unexpected token`).
