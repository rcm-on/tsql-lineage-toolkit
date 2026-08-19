---
title: Verificación rápida
description: Los cuatro comandos de 30 segundos para dar el visto bueno a un cambio, más las trampas del entorno.
read_when: Antes de dar por bueno cualquier cambio en el motor, como primer paso.
related: [docs/guia-de-verificacion.md, docs/ejecucion-canonica.md]
stability: durable
updated: 2026-08-19
---

# Verificación

Esto es el mínimo de 30 segundos. El checklist completo — corpus, `validate`, capturas del
dashboard, higiene de git, trampas ambientales por síntoma — vive en
`docs/guia-de-verificacion.md`; no lo dupliques aquí.

## Comandos

```bash
dotnet build ParserGeneral.sln -c Release
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --filter "Category!=LiveSql"
dotnet test tests/NetParser.Tests/NetParser.Tests.csproj -c Release
dotnet src/TSqlParser/bin/Release/net10.0/TSqlParser.dll blind-refs dnn out.csv
```

Cifras de referencia en el momento de escribir esto: **268 / 43 / 90 ciegas
(98,7675 % de recall laxo)**. `Category!=LiveSql` excluye los tests que necesitan un SQL
Server real.

## Trampas del entorno

- **Smart App Control bloquea el DLL de Debug recién compilado** (`FileLoadException`
  `0x800711C7`). No es un fallo del código. Invoca el DLL de **Release** directamente.
  Detección y reintentos: `docs/guia-de-verificacion.md` §2.
- **No hay SQL Server local en esta máquina.** La vía viva es el contenedor
  (`scripts/ci/restore-sample-databases.sh`, necesita Docker Desktop arrancado).
- **`notes/` está ignorado por git.** Lo que deba sobrevivir a la máquina va en `docs/`.
- **La rama por defecto es `main`**, no `master`.
