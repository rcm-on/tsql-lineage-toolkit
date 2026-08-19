#!/usr/bin/env bash
# Downloads WideWorldImporters-Full.bak and AdventureWorks2019.bak from the public
# microsoft/sql-server-samples releases, copies them into the running mssql service
# container (see .github/workflows/ci.yml, job oracle-tests), and RESTOREs both
# databases so ViewLineageOracleTests / AuditorChallengeGateTests can run against them.
#
# Requires:
#   - a SQL Server container already running and reachable on localhost:1433
#     (the `mssql` service in the oracle-tests job), discoverable via
#     `docker ps --filter publish=1433`
#   - MSSQL_SA_PASSWORD env var: the sa password the container was started with
#
# Verified locally on 2026-08-16 against mcr.microsoft.com/mssql/server:2022-latest,
# both databases restored and all 3 Oracle-tagged tests passing, using a SA password
# containing ';' and '"' (exercises SqlConnections.Quote in src/TSqlParser).
set -euo pipefail

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD no está definida}"

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

WWI_URL="https://github.com/Microsoft/sql-server-samples/releases/download/wide-world-importers-v1.0/WideWorldImporters-Full.bak"
AW_URL="https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2019.bak"

echo "Descargando backups..."
curl -fsSL -o "$WORKDIR/WideWorldImporters-Full.bak" "$WWI_URL"
curl -fsSL -o "$WORKDIR/AdventureWorks2019.bak" "$AW_URL"

# Everything below passes in-container paths (/opt/..., /var/opt/mssql/...) to `docker
# exec`. On a plain Linux runner (the real CI target) this is a no-op; it only matters
# when this script is exercised locally from Git-Bash on Windows, whose MSYS layer
# otherwise rewrites leading-/ arguments into host Windows paths before they reach
# docker.exe, breaking the container-side sqlcmd invocation.
export MSYS_NO_PATHCONV=1

# `docker cp`'s source argument is a host path, which - unlike the container-side
# paths above - DOES need MSYS's translation. cygpath only exists on Git-Bash/MSYS
# (i.e. only when this script is run locally on Windows); on the real Linux CI
# runner this branch is never taken and $1 is returned unchanged.
host_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s' "$1"
  fi
}

CONTAINER_ID="$(docker ps --filter "publish=1433" -q | head -n1)"
if [ -z "$CONTAINER_ID" ]; then
  echo "No se encontró el contenedor de SQL Server (servicio mssql, puerto 1433)" >&2
  exit 1
fi

SQLCMD=(docker exec "$CONTAINER_ID" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C)

echo "Esperando a que SQL Server acepte conexiones..."
ready=0
for i in $(seq 1 30); do
  if "${SQLCMD[@]}" -Q "SELECT 1" >/dev/null 2>&1; then
    echo "SQL Server listo tras $i intento(s)."
    ready=1
    break
  fi
  sleep 10
done
if [ "$ready" -ne 1 ]; then
  echo "SQL Server no respondió tras 30 intentos (~5 min)" >&2
  exit 1
fi

docker exec "$CONTAINER_ID" mkdir -p /var/opt/mssql/backup
docker cp "$(host_path "$WORKDIR/WideWorldImporters-Full.bak")" "$CONTAINER_ID":/var/opt/mssql/backup/WideWorldImporters-Full.bak
docker cp "$(host_path "$WORKDIR/AdventureWorks2019.bak")" "$CONTAINER_ID":/var/opt/mssql/backup/AdventureWorks2019.bak

# Logical file names confirmed via RESTORE FILELISTONLY against these exact backups
# (2026-08-16): WWI_Primary/WWI_UserData/WWI_Log/WWI_InMemory_Data_1 and
# AdventureWorks2019/AdventureWorks2019_log.
cat > "$WORKDIR/restore-wwi.sql" <<'SQL'
RESTORE DATABASE WideWorldImporters
FROM DISK = '/var/opt/mssql/backup/WideWorldImporters-Full.bak'
WITH
  MOVE 'WWI_Primary' TO '/var/opt/mssql/data/WideWorldImporters.mdf',
  MOVE 'WWI_UserData' TO '/var/opt/mssql/data/WideWorldImporters_UserData.ndf',
  MOVE 'WWI_Log' TO '/var/opt/mssql/data/WideWorldImporters.ldf',
  MOVE 'WWI_InMemory_Data_1' TO '/var/opt/mssql/data/WideWorldImporters_InMemory_Data_1',
  REPLACE,
  RECOVERY,
  STATS = 10;
GO
SQL

cat > "$WORKDIR/restore-aw.sql" <<'SQL'
RESTORE DATABASE AdventureWorks2019
FROM DISK = '/var/opt/mssql/backup/AdventureWorks2019.bak'
WITH
  MOVE 'AdventureWorks2019' TO '/var/opt/mssql/data/AdventureWorks2019.mdf',
  MOVE 'AdventureWorks2019_log' TO '/var/opt/mssql/data/AdventureWorks2019_log.ldf',
  REPLACE,
  RECOVERY,
  STATS = 10;
GO
SQL

docker cp "$(host_path "$WORKDIR/restore-wwi.sql")" "$CONTAINER_ID":/var/opt/mssql/backup/restore-wwi.sql
docker cp "$(host_path "$WORKDIR/restore-aw.sql")" "$CONTAINER_ID":/var/opt/mssql/backup/restore-aw.sql

echo "Restaurando WideWorldImporters..."
"${SQLCMD[@]}" -i /var/opt/mssql/backup/restore-wwi.sql

echo "Restaurando AdventureWorks2019..."
"${SQLCMD[@]}" -i /var/opt/mssql/backup/restore-aw.sql

echo "Ambas bases restauradas."
