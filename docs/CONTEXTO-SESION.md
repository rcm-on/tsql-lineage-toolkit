# Contexto de sesión

**Léeme primero.** Este fichero existe para que una sesión nueva arranque sin tener que
explorar el repo. Si algo aquí contradice al código, gana el código y **corrige este
fichero en el mismo commit**.

Mapa de lectura, por orden y solo lo que necesites:

| Necesitas | Fichero |
|---|---|
| Qué es el producto y para quién | `README.md` |
| Dónde vive cada cosa y qué puede depender de qué | `docs/ARQUITECTURA.md` |
| Qué hay que hacer y en qué orden | `docs/plan-arquitectura.md` |
| Qué pasó en las sesiones anteriores | `docs/BITACORA.md` (lo más reciente arriba) |
| Cómo se verifica que sigue todo bien | este fichero, sección "Verificación" |

---

## 1. Qué es esto en dos frases

Motor determinista de lineage e impacto para T-SQL: lee procedimientos (de un SQL Server
vivo o de ficheros `.sql`), construye un grafo consultable de qué lee qué / qué escribe
dónde / qué se rompe si lo cambias, hasta nivel de columna y a través del SQL dinámico, y
lo entrega como artefactos portables (JSON, nodestore, SQLite, dashboard) más un servidor
MCP para que un agente lo consulte en conversación.

El objetivo real no es "extraer lineage": es **soporte a la decisión para un LLM** —
impacto, remediación ordenada y visión macro. Por eso la completitud de la extracción es
la prioridad de solidez número uno, y por eso un resultado vacío nunca puede leerse como
"no hay impacto".

## 2. Estructura de la solución

Cinco proyectos, con la dependencia en una sola dirección. El detalle y las reglas están
en `docs/ARQUITECTURA.md`; el resumen mínimo:

```
Parser.Contracts   modelo + vocabulario + StoreSchema. Cero dependencias.
Parser.Graph       capa agnóstica del lenguaje: exportadores, riesgo, change-map,
                   auditoría, bench. Solo Contracts + Sqlite.
Parser.Mcp         servidor MCP. Solo Contracts + Sqlite. NO puede ver el parser.
TSqlParser         extractor T-SQL (ScriptDom) + acceso a SQL Server vivo + CLI.
NetParser          extractor de C# (Roslyn).
ParserGeneral      compone los dos extractores en un grafo unificado.
```

Regla que lo gobierna: **los extractores no conocen los sinks**. Si un cambio te obliga a
que `Parser.Graph` o `Parser.Mcp` referencien `TSqlParser`, el cambio está mal planteado.

## 3. Verificación (30 segundos)

```bash
dotnet build ParserGeneral.sln -c Release
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=LiveSql"
dotnet test tests/NetParser.Tests/NetParser.Tests.csproj -c Release
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll blind-refs dnn out.csv
```

Cifras de referencia en el momento de escribir esto: **268 / 43 / 90 ciegas
(98,7675 % de recall laxo)**. `Category!=LiveSql` excluye los tests que necesitan un SQL
Server real.

## 4. Trampas del entorno (cuestan una hora si no las sabes)

- **Smart App Control bloquea el DLL de Debug recién compilado** (`FileLoadException`
  `0x800711C7`). No es un fallo del código. Invoca el DLL de **Release** directamente.
- **No hay SQL Server local en esta máquina.** La vía viva es el contenedor
  (`scripts/ci/restore-sample-databases.sh`, necesita Docker Desktop arrancado).
- **`notes/` está ignorado por git.** Lo que deba sobrevivir a la máquina va en `docs/`.
- **La rama por defecto es `main`**, no `master`.

## 5. Cómo se trabaja aquí

- **Gates ejecutados, no prometidos.** Ningún "debería funcionar": se ejecuta y se pega el
  número.
- **Prueba por mutación en cada arreglo del motor.** Rompe a propósito lo que el gate debe
  cazar y comprueba que se pone rojo con el mensaje correcto. Un gate que no se ha visto
  fallar no es un gate.
- **Un resultado vacío es un instrumento roto hasta demostrar lo contrario.** Cero
  hallazgos se investiga; no se celebra.
- **Verificación contra SQL vivo además del corpus congelado.** El corpus no ve las
  regresiones que el catálogo sí.
- **Cada paso compila, pasa la suite y se commitea solo.** Nada de refactores en bloque:
  un corte a mitad debe dejar el árbol sano.
- **Checkpoints en `notes/checkpoints/<tarea>.md` desde el primer dato**, no al final.
- **Al terminar la sesión se escribe siempre una entrada en `docs/BITACORA.md`**: qué se
  hizo, qué se aprendió que no estaba previsto, en qué estado queda el árbol y cuál es el
  siguiente paso concreto. Sin excepciones, aunque la sesión haya sido corta o haya
  fracasado — una sesión sin entrada hay que reconstruirla leyendo commits.
- **Nada de atribución de IA** en commits ni PRs. Autor: Ramón Campos Martín.
- **Comentarios escuetos.** El porqué va al commit o a `notes/`, no a un bloque de `///`.

## 6. Dónde está la cola

`docs/plan-arquitectura.md`, sección "Orden de ejecución". Estado resumido al día de hoy:

- **Fase 0 (arquitectura)**: pasos 0.1 a 0.8 hechos. Queda `IGraphSink` y el paso 0.9
  (`ParserGeneral` escribiendo SQLite del grafo unificado), que es **la prueba de que la
  arquitectura sirve**: si sale difícil, hay que revisarla antes de seguir.
- **Fase 1 (producto)**: T17 herramientas de columna del MCP, T18 `diff_impact`,
  `store_info` + `describe_object`, `quickstart` + prompts + documentación del MCP,
  `IRiskRule` + `risks`.
- **Fase 2 (con red)**: partir `GraphExporter.Build` (~1167 líneas en un método), migrar
  `AstWalker` a visitors de ScriptDom, `ISqlCatalog`.

## 7. Decisiones abiertas

- `test/pr-impact-demo`: única copia publicada de documentos que hoy solo viven en
  `notes/`. Rescatar a `docs/` o dejar como archivo.
- Blog (`quarz-blog`): 35 sustituciones aplicadas y sin commitear; falta decidir el rótulo
  `Gate / Oráculo` del diagrama Mermaid.
- NuGet: aparcado. Cuando toque, `0.1.0-preview.1`.
