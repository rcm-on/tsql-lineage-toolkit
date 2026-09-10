# Imagen del CLI (tsql-lineage). Se construye desde la raíz del repo:
#   docker build -f infra/docker/Dockerfile -t tsql-lineage:dev .
#   docker run --rm -v "$PWD:/work" -w /work tsql-lineage:dev syntax-coverage input.json --anon
#
# Es la vía para ejecutar el motor donde no se puede instalar el SDK de .NET — que es
# justo el caso de una máquina corporativa ajena.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Primero los proyectos, para que la capa de restore se reutilice mientras no cambien.
COPY ParserGeneral.sln ./
COPY src/ ./src/
# TSqlParser.csproj embebe eval/column-recall/extract-catalog.sql como recurso: el tool
# instalado no tiene el repositorio al lado. Sin esto, el publish falla con CS1566.
COPY eval/column-recall/extract-catalog.sql ./eval/column-recall/
RUN dotnet restore src/TSqlParser/TSqlParser.csproj

RUN dotnet publish src/TSqlParser/TSqlParser.csproj \
    -c Release \
    -o /app \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /work

COPY --from=build /app /opt/tsql-lineage

# No root: el contenedor solo necesita leer .sql y escribir informes. Se usa el usuario
# "app" que ya trae la imagen de Microsoft; crear uno con UID 1000 falla porque ese UID
# ya está ocupado en la base.
USER app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

ENTRYPOINT ["dotnet", "/opt/tsql-lineage/TSqlParser.dll"]
CMD ["--help"]
