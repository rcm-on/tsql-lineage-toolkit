# eng/

Todo lo que no es el producto: cómo se compila, cómo se prueba y cómo se empaqueta.

```
Dockerfile             (raíz)  Imagen del CLI, sobre runtime de .NET
compose.yaml           (raíz)  Entorno de desarrollo: SQL Server + SDK de .NET
.env.example           (raíz)  Plantilla de credenciales; copiar a .env
eng/                           Infraestructura de ingeniería
  restore-sample-databases.sh  Restaura WideWorldImporters y AdventureWorks2019
.github/workflows/             Los circuitos de CI/CD
  ci.yml                       Build, tests y gates
  pr-impact.yml                Comentario de impacto en la PR
```

## Por qué cada cosa está donde está

`eng/` es la convención de la casa en .NET: `dotnet/runtime`, `dotnet/aspnetcore` y
`roslyn` guardan ahí su infraestructura de ingeniería. `infra/` e `infrastructure/` son la
convención cuando hay IaC de verdad —Terraform, Bicep—, y `deploy/` cuando hay manifiestos
de Kubernetes; aquí no hay ni lo uno ni lo otro, así que usarlos prometería algo que no
existe.

El `Dockerfile` va en la raíz porque es el sitio por defecto de `docker build .` y lo que
asume cualquier registro o sistema de build. `compose.yaml` también: `docker compose` lo
descubre solo desde el directorio actual, y moverlo obliga a escribir `-f eng/compose.yaml`
en cada invocación y a reescribir todas las rutas de volumen.

`.github/workflows/` no se puede mover: GitHub exige esa ruta exacta.

## Compilar y probar

```bash
cp .env.example .env    # y edita la contraseña
docker compose run --rm sdk dotnet build ParserGeneral.sln -c Release --artifacts-path /repo/.artifacts-linux
docker compose run --rm sdk dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release --artifacts-path /repo/.artifacts-linux --filter "Category!=LiveSql"
```

`--artifacts-path` apunta **dentro** del repo a propósito: varios tests localizan la raíz
subiendo desde el ensamblado y desde un volumen externo nunca la encuentran. Pero va a su
propia carpeta (`.artifacts-linux/`, ignorada por git) porque el build de Linux y el de
Windows no comparten rutas ni RIDs, y mezclarlos deja los dos rotos.

En Windows, compilar en el contenedor no es una preferencia: Smart App Control bloquea
cualquier binario recién compilado con `FileLoadException 0x800711C7`. Ver `AGENTS.md`.

## Con base viva

```bash
docker compose up -d mssql
MSSQL_SA_PASSWORD=... bash eng/restore-sample-databases.sh
docker compose run --rm sdk dotnet run --project src/TSqlParser -c Release \
  --artifacts-path /repo/.artifacts-linux -- recall WideWorldImporters --server mssql
```

Dentro de la red de compose el servidor se llama `mssql`, no `localhost`. El script de
restauración se ejecuta **desde el host** (usa `docker cp` contra el contenedor que publica
el 1433), no dentro del servicio `sdk`.

## La imagen del CLI

```bash
docker build -t tsql-lineage:dev .
docker run --rm -v "$PWD:/work" -w /work tsql-lineage:dev syntax-coverage input.json --anon
```

Existe para ejecutar el motor donde no se puede instalar el SDK de .NET, que es el caso
habitual de una máquina corporativa ajena.
