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

Si añades una capacidad de extracción, **añade un caso al corpus** correspondiente en `eval/` (con su oráculo) para que quede cubierto por el gate de CI. Los corpus se declaran en [`eval/corpora.json`](eval/corpora.json); ver [`eval/README.md`](eval/README.md).

## La regla del cero culpable

Este proyecto vive de medir. Por eso la regla que más protege no es de estilo, es de método:

> **Un resultado vacío es culpable de instrumento roto hasta que se demuestre lo contrario.**
>
> Cero coincidencias, cero diferencias, "sin deriva", "no encontrado", denominador cero. Antes de reportarlo, corre el mismo instrumento contra una entrada que **tiene que** dar positivo. Si tampoco la encuentra, ese cero no medía nada.

No es una precaución teórica. Ha mordido tres veces:

- `lineage_coverage` informaba `100 %` sobre `0/0` objetos. Hoy devuelve `null` con `measured: false`.
- El comparador de corpus buscaba la propiedad `"name"` cuando el JSON lleva `"Name"`, y anunciaba *"congelado=0, base viva=0, sin deriva"* — es decir, aprobado.
- Un `grep` mal escrito devolvió cero nodos entre bases y estuvo a punto de publicarse como un defecto del motor. El motor los modelaba: 19 tablas y 50 columnas.

Lo que se te pide en un PR:

1. **Toda función que pueda devolver "vacío" por no encontrar nada Y por estar rota tiene que distinguir los dos casos**: error duro, `null`, o un campo `measured: false`. Nunca un cero silencioso.
2. **Todo gate necesita un control que solo pueda fallar.** Si tu test no puede fallar, no es un gate. Los de aquí perturban el oráculo (`Measurement_IsSensitive_ControlThatMustCollapse`) o comparan contra un caso de referencia (`CrossDatabaseLineageTests`).
3. **Un suelo se sube con una cifra medida, nunca copiada de una regeneración.** Y el commit que actualiza un corpus no toca el motor: si los dos se mueven a la vez, la cifra nueva no atribuye nada.

## Pull requests

- Rama desde `main`, un PR enfocado por cambio.
- El CI (build + tests + corpus de `eval/`) debe pasar.
- Sin dependencias nuevas salvo que sean imprescindibles.

## Licencia

Al contribuir, aceptas que tu aportación se publique bajo la licencia **MIT** del proyecto.
