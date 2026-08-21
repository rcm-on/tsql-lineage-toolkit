---
title: Convenciones de trabajo
description: Reglas de proceso del repo — gates, mutación, checkpoints, bitácora, autoría.
read_when: Antes de empezar cualquier tarea en el repo, y al terminar una sesión.
related: [docs/BITACORA.md, docs/VERIFICACION.md]
stability: durable
updated: 2026-08-21
---

# Cómo se trabaja aquí

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
- **Reclasificar siempre antes de seguir arreglando.** Después de cada arreglo que mueva
  el contador, la clasificación anterior **ya no describe lo que queda**: se vuelve a medir
  el reparto antes de tocar nada. Un diagnóstico caducado lleva a arreglar el síntoma que
  ya no es el mayor. Y clasificar dos veces cuesta minutos; arreglar lo que no tocaba,
  horas.
- **Diagnosticar desde el código, no desde los síntomas.** Cuando el mismo fallo aparece en
  varios sitios, buscar el punto donde convergen **antes** de arreglar el primero. El
  corpus dice "cuatro patrones" y lleva a cuatro parches; el código dice "un punto de
  convergencia". Medido: los cuatro patrones ciegos de columna eran un solo mecanismo al
  que le faltaba la mitad, y arreglarlo ahí se llevó de golpe lo que iban a ser cuatro
  cambios.
- **Terminología: "referencia", nunca "oráculo".** La fuente de verdad contra la que se
  contrasta se llama **catálogo** cuando es el de SQL Server, y **referencia** o **fuente de
  referencia** en general; la categoría de pruebas contra base viva es `LiveSql`. "Oráculo"
  suena a adivinación y describe justo lo contrario de lo que hace: un contraste
  determinista y verificable. La palabra ya se retiró una vez y volvió a colarse.
- **Nada de atribución de IA** en commits ni PRs. Autor: Ramón Campos Martín.
- **Comentarios escuetos.** El porqué va al commit o a `notes/`, no a un bloque de `///`.
