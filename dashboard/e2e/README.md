# Dashboard e2e smoke test

Abre `dashboard/index.html` con Chromium (Playwright), sube
`samples/from-sql-demo/graph.json` y comprueba que la app sale del estado
"sin datos" (clase `loaded` en `<body>`, subtítulo con el resumen, sin
diálogos de error ni errores de consola). Guarda `screenshot.png` como
evidencia.

## Uso

```bash
cd dashboard/e2e
npm install
npm test
```
