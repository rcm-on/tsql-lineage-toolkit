# Contribuir

Gracias por probar el **T-SQL Lineage Toolkit**. La forma más útil de contribuir es **darle SQL real**.

## El caso más valioso: "no me extrae esto bien"

La completitud es alta pero no total. Si apuntas el toolkit a tu base de datos y encuentras un objeto que no extrae bien —una tabla que se pierde, un lineage que se corta, un patrón raro—, **ábrelo como issue** con:

1. El fragmento de T-SQL (anonimizado si hace falta) que reproduce el problema.
2. Qué esperabas que detectara.
3. Qué salió (o qué faltó).

Esos casos reales son el mejor corpus: cada uno se convierte en un test de regresión y estrecha el hueco.

## Desarrollo

```bash
# Construir
dotnet build src/TSqlParser/TSqlParser.csproj -c Release

# Tests (xUnit) — deben pasar en verde antes de un PR
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release
```

Si añades una capacidad de extracción, **añade un caso al corpus** correspondiente en `eval/` (con su oráculo) para que quede cubierto por el gate de CI.

## Pull requests

- Rama desde `master`, un PR enfocado por cambio.
- El CI (build + tests + corpus de `eval/`) debe pasar.
- Sin dependencias nuevas salvo que sean imprescindibles.

## Licencia

Al contribuir, aceptas que tu aportación se publique bajo la licencia **MIT** del proyecto.
