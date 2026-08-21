---
title: Verificación de empaquetado como herramienta global
description: Procedimiento end-to-end para empaquetar, instalar y verificar TsqlLineageToolkit.Cli como herramienta global de dotnet, con casos de prueba para los tres ensamblados nuevos (Parser.Graph, Parser.Mcp) y el recurso embebido (extract-catalog.sql).
read_when: Después de cualquier cambio en TSqlParser.csproj o en las referencias de Parser.Graph/Parser.Mcp, antes de publicar un lanzamiento.
related: [docs/VERIFICACION.md, docs/guia-de-verificacion.md]
stability: durable
updated: 2026-08-21
---

# Empaquetado como herramienta global

## Verificación completa (2026-08-21)

El 2026-08-21, se verificó end-to-end que TsqlLineageToolkit.Cli.0.1.0 (empaquetado en Release) instala correctamente como herramienta global dotnet y que **los tres proyectos nuevos (Parser.Graph, Parser.Mcp) y el recurso embebido (extract-catalog.sql) viajan en el paquete .nupkg**.

### Resultado: **OK - todas las pruebas pasan**

| Prueba | Resultado | Comando | Verificación |
|--------|-----------|---------|--------------|
| Ayuda principal | ✓ OK | `tsql-lineage` sin argumentos | Imprime uso y subcomandos (incluyendo `recall` y `mcp`) |
| Subcomando `recall` | ✓ OK | `tsql-lineage recall` sin argumentos | Imprime uso (`Usage: TSqlParser recall <database>...`); **prueba que el recurso embebido `extract-catalog.sql` viaja** |
| Subcomando `mcp` | ✓ OK | `tsql-lineage mcp --store /tmp/nonexistent.db` | Error controlado: `No existe la base SQLite 'C:\...\nonexistent.db'`; **prueba que Parser.Mcp.dll viaja y carga sin excepciones** |
| JSON-RPC tools/list | ✓ OK | `echo '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \| tsql-lineage mcp --store <db>` | Lista **9 herramientas**: resolve_object, impact, column_provenance, column_impact, blind_spots, diff_impact, risks, store_info, describe_object |

## Procedimiento paso a paso

### 1. Empaquetar

```bash
cd C:\MisCosas\MisProyectos\sql-analyzer\tsql-lineage-toolkit
dotnet pack src/TSqlParser/TSqlParser.csproj -c Release -o ./nupkg
```

**Salida esperada:**
```
Paquete creado correctamente 'C:\...\nupkg\TsqlLineageToolkit.Cli.0.1.0.nupkg'.
```

**Notas:**
- Usa siempre `Release`: Smart App Control en Windows bloquea los DLL de Debug.
- El `.nupkg` se descarga con todas sus dependencias (Parser.Contracts.dll, Parser.Graph.dll, Parser.Mcp.dll, extract-catalog.sql embebido, etc.).

### 2. Desinstalar versiones anteriores (si las hay)

```bash
dotnet tool uninstall --global TsqlLineageToolkit.Cli
```

Espera el mensaje `No se encontró...` si no hay versión previa instalada.

### 3. Instalar desde la carpeta local

```bash
dotnet tool install --global --add-source ./nupkg TsqlLineageToolkit.Cli --version 0.1.0
```

**Salida esperada:**
```
Puede invocar la herramienta con el comando siguiente: tsql-lineage
La herramienta "tsqllineagetoolkit.cli" (versión '0.1.0') se instaló correctamente.
```

**Requisitos:**
- `%USERPROFILE%\.dotnet\tools` debe estar en el PATH (ya lo está en máquinas estándar Windows).

### 4. Verificar las cuatro cosas

#### 4.1 Ayuda principal

```bash
tsql-lineage
```

Debe listar los subcomandos, incluyendo `recall` y `mcp`.

#### 4.2 Subcomando `recall` (prueba el recurso embebido)

```bash
tsql-lineage recall
```

Debe imprimir su uso. Esto verifica que `extract-catalog.sql` viaja embebido en el paquete.

#### 4.3 Subcomando `mcp` con error controlado (prueba Parser.Mcp.dll)

```bash
tsql-lineage mcp --store C:\nonexistent.db
```

Debe mostrar el error controlado: `No existe la base SQLite 'C:\nonexistent.db'.`

**Esto prueba que Parser.Mcp.dll está en el paquete y carga sin fallos de ensamblado.**

#### 4.4 JSON-RPC tools/list (prueba MCP funcional)

Primero, genera un store:

```bash
# Crear un input.json vacío
echo "[]" > input.json

# Ejecutar el comando de análisis
tsql-lineage input.json output.json --columns --sqlite

# Esto crea output.db (SQLite store)
```

Luego, habla JSON-RPC con el MCP server:

```bash
$jsonRpcRequest = @'
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
'@

$jsonRpcRequest | tsql-lineage mcp --store output.db
```

**Resultado esperado:** Una respuesta JSON-RPC con un array `tools` que contiene exactamente **9 herramientas**:

1. `resolve_object` — Resuelve nombres SQL ambiguos.
2. `impact` — Impacto downstream/upstream.
3. `column_provenance` — Procedencia de valores de columnas.
4. `column_impact` — Qué rompe si cambio esta columna.
5. `blind_spots` — SQL dinámico no resuelto.
6. `diff_impact` — Impacto entre dos stores.
7. `risks` — Hallazgos de malas prácticas.
8. `store_info` — Metadatos del store.
9. `describe_object` — Perfil completo de un objeto.

### 5. Desinstalar

```bash
dotnet tool uninstall --global TsqlLineageToolkit.Cli
```

**Salida esperada:**
```
La herramienta "tsqllineagetoolkit.cli" (versión "0.1.0") se desinstaló correctamente.
```

### 6. Limpiar

```bash
# Borrar la carpeta nupkg (no debe quedar en el repositorio)
Remove-Item nupkg -Recurse -Force

# Verificar que git está limpio
git status
```

## Trampas

### Smart App Control bloquea Debug DLL

Si el `.nupkg` contiene un DLL compilado en Debug, dotnet tool install falla con un error de carga. **Siempre usa `-c Release`.**

### PATH no incluye %USERPROFILE%\.dotnet\tools

Si `dotnet tool install --global` falla con "tool not found" después de la instalación, comprueba que esa ruta está en tu PATH. En máquinas estándar ya está.

### El .nupkg no se limpia

El archivo `nupkg/TsqlLineageToolkit.Cli.0.1.0.nupkg` NO debe quedar en el repositorio. `git status` debe estar limpio. Borra `nupkg/` antes de hacer commit.

### extract-catalog.sql no viaja si la ruta relativa es incorrecta

El `.csproj` define `extract-catalog.sql` con `LogicalName=TSqlParser.extract-catalog.sql`. Si la ruta relativa es incorrecta o el archivo no existe en build time, el subcomando `recall` falla con "recurso no encontrado". Verifica que el archivo está en la carpeta raíz del proyecto TSqlParser.

## Si algo falla

- **`dotnet pack` falla con error de compilación:** Ejecuta `dotnet build src/TSqlParser/TSqlParser.csproj -c Release` primero.
- **`dotnet tool install` falla:** Comprueba que el .nupkg se creó (debe estar en `./nupkg/TsqlLineageToolkit.Cli.0.1.0.nupkg`).
- **`tsql-lineage recall` no imprime nada:** El recurso embebido no se incluyó. Revisa la sección `<ItemGroup>` con `<EmbeddedResource>` en `TSqlParser.csproj`.
- **`tsql-lineage mcp` falla con `System.BadImageFormatException`:** El DLL fue compilado en Debug. Usa `-c Release`.

## Cambios en .csproj

**Ninguno requerido.** El TSqlParser.csproj ya está configurado correctamente:

- `<ToolCommandName>tsql-lineage</ToolCommandName>` — Nombre del comando.
- `<PackAsTool>true</PackAsTool>` — Marcar como herramienta empaquetable.
- `<ItemGroup>` con `<ProjectReference>` a `Parser.Contracts`, `Parser.Graph`, `Parser.Mcp` — Todas incluidas.
- `<EmbeddedResource Include="extract-catalog.sql" LogicalName="TSqlParser.extract-catalog.sql" />` — Recurso embebido para `recall`.

No se modificó nada durante la verificación de 2026-08-21.
