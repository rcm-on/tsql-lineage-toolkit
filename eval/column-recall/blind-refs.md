# Referencias de columna ciegas — clasificación por causa (medición, no arreglo)

Fecha de medición: **2026-08-16**. Solo mide; no se ha tocado `src/**` ni ningún test
existente. Cifras medidas en esta sesión, no reutilizadas de notas previas.

## Comando de reproducción

El **agregado** (recall laxo, suelo) es el que ya corre el gate:

```bash
dotnet test tests/TSqlParser.Tests/TSqlParser.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ColumnRecallGateTests"
```

Con eso en verde (8/8 esta sesión) se confirma que el corpus/oráculo congelados
siguen intactos y que el recall laxo medido aquí no contradice el suelo del
manifiesto (`eval/corpora.json`).

El **listado** de qué (módulo, columna) del oráculo queda fuera del grafo —que
el gate no vuelca, solo el conteo agregado— se obtuvo con un programa de un
solo uso que reimplementaba
literalmente `LoadOracle` + `BuildGraphRefs` de
`tests/TSqlParser.Tests/ColumnRecallGateTests.cs` en un `Program.cs` de consola
que referencia `src/TSqlParser/TSqlParser.csproj` como `ProjectReference`,
llama a `InputAnalyzer.Analyze` + `GraphExporter.Build` sobre
`eval/column-recall/dnn-corpus.json`, y vuelca a fichero
`oracleLoose - graphLoose` (el conjunto laxo del oráculo que el motor no cubre).
Reproducible copiando esa lógica a un proyecto consola cualquiera; no requiere
SQL Server.

## DNN Platform — resultado agregado

```
oráculo (bruto)          = 7786
oráculo laxo (mod,col)   = 7302
grafo (bruto)            = 10296
grafo laxo (mod,col)     = 7638
recall laxo              = 98,0827 %   (suelo del manifiesto: 98,08 %)
BLIND (laxo)             = 140
```

**Nota de discrepancia**: una nota anterior citaba "~161" ciegas de una nota anterior.
Medido en esta sesión son **140**, no 161. La cifra 140 sí es coherente con el
suelo publicado en `eval/corpora.json` (0,9808, truncado por debajo del valor
medido — 140/7302 ciegas da exactamente 98,08 %), así que el número viejo
(161) está desactualizado, no el instrumento de esta sesión.

## Clasificación por causa raíz (140 de 140, sin residuo)

| # | Causa | Cuántas | % |
|---|---|---:|---:|
| 1 | Subconsulta anidada en predicado o expresión (`EXISTS(...)`, `NOT EXISTS(...)`, `IN (SELECT ...)`, comparación escalar `= (SELECT ...)`, derived table en `FROM`/`UPDATE`) — sus propias columnas no se recorren | 58 | 41,4 % |
| 2 | `MERGE`: ni la condición `ON` ni el `WHEN MATCHED THEN UPDATE SET` generan arista | 23 | 16,4 % |
| 3 | Función de tabla (TVF) invocada como origen de filas (`FROM func(...)`, `CROSS/OUTER APPLY`, `JOIN func(...)`) — columnas de salida no resueltas | 17 | 12,1 % |
| 4 | Vista cuya propia definición tiene una subconsulta o derived table anidada — la columna no se atribuye a la vista | 16 | 11,4 % |
| 5 | `SELECT *` sobre una vista cuya expansión no incluye una columna concreta de esa vista (agregada vía derived table o `CASE`+subconsulta) | 8 | 5,7 % |
| 6 | CTE consumida por `DELETE ... FROM cte` o por un cursor (`FETCH INTO`) — no todas las columnas de la CTE se propagan | 8 | 5,7 % |
| 7 | Columna referenciada solo en `ORDER BY`, nunca en `SELECT`/`WHERE` | 3 | 2,1 % |
| 8 | Otras — sin causa raíz aislada con confianza (ver nota) | 3 | 2,1 % |
| 9 | Lectura dentro de una función escalar (UDF) invocada — el oráculo la atribuye al módulo llamante, el motor no la propaga | 2 | 1,4 % |
| 10 | `OUTPUT inserted.<col>` — pseudo-tabla de un `INSERT` no resuelta | 1 | 0,7 % |
| 11 | `UPDATE ... SET @variable = columna` (variante de `SELECT @var = columna`, no soportada en `UPDATE`) | 1 | 0,7 % |
| | **Total** | **140** | **100 %** |

### Ejemplos (uno por causa, objeto + columna + fragmento ≤3 líneas)

**1. Subconsulta anidada en predicado/expresión** — `dbo.DeleteDesktopModule`, columna `moduledefid` (dbo.ModuleDefinitions):
```sql
DELETE FROM dbo.Permission
WHERE moduledefid in (SELECT moduledefid FROM dbo.ModuleDefinitions WHERE desktopmoduleid = @DesktopModuleId)
```

**2. MERGE** — `dbo.UpdateHostSetting`, columna `settingvalue` (dbo.HostSettings):
```sql
MERGE INTO dbo.[HostSettings] S USING (SELECT @SettingName SN, @SettingValue SV, ...) Q ON (S.SettingName = Q.SN)
 WHEN MATCHED ... THEN UPDATE SET [SettingValue] = Q.SV, [LastModifiedByUserID] = @UserID, [LastModifiedOnDate] = GetDate()
```

**3. TVF como origen de filas** — `dbo.CoreMessaging_CreateMessageRecipientsForRole`, columna `item` (dbo.SplitStrings_CTE):
```sql
FROM dbo.[vw_UserRoles] ur
INNER JOIN dbo.[SplitStrings_CTE](@RoleIDs,',') m on ur.RoleID = m.Item
```

**4. Vista con subconsulta/derived table anidada** — `dbo.vw_ApiTokens`, columna `portalname` (dbo.PortalLocalization):
```sql
LEFT JOIN (SELECT pl.PortalID, pl.PortalName FROM dbo.[Portals] p
 INNER JOIN dbo.[PortalLocalization] pl ON p.PortalID=pl.PortalID AND pl.CultureCode=p.DefaultLanguage) portals ON portals.PortalID=a.PortalId
```

**5. SELECT * sobre vista, columna no expuesta** — `dbo.GetPortals`, columna `supertabid` (dbo.vw_Portals):
```sql
SELECT * FROM dbo.[vw_Portals]
WHERE CultureCode = CASE WHEN IsNull(@CultureCode, N'') = N'' THEN DefaultLanguage ELSE @CultureCode END
```
(`vw_Portals` sí calcula `SuperTabID`; el catálogo de columnas que expande el `*` lo pierde.)

**6. CTE consumida por DELETE/cursor** — `dbo.PurgeEventLog`, columna `logconfigid` (dbo.EventLog):
```sql
;WITH logcounts AS (SELECT TOP(@PurgeBatchCount) LogConfigID, ROW_NUMBER() OVER(PARTITION BY LogConfigID ...) FROM dbo.[EventLog])
DELETE lc FROM logcounts lc INNER JOIN dbo.[EventLogConfig] elc ON elc.ID = lc.LogConfigID
```

**7. Columna solo en ORDER BY** — `dbo.GetTabUrls`, columna `taborder` (dbo.Tabs):
```sql
FROM dbo.TabUrls tu INNER JOIN dbo.Tabs t on t.TabId = tu.TabId ...
ORDER BY PortalId, TabOrder, tu.SeqNum
```

**8. Otras (sin causa aislada)** — `dbo.GetTabCustomAliases`, columna `httpalias` (dbo.PortalAlias):
```sql
SELECT t.TabId, Coalesce(trp.CultureCode, '') as CultureCode, pa.HttpAlias
FROM dbo.Tabs t INNER JOIN dbo.TabUrls trp ON trp.TabId = t.ParentId INNER JOIN dbo.PortalAlias pa ON trp.PortalAliasId = pa.PortalAliasId
```
Es un `JOIN` llano dentro de una consulta con `UNION`; `CultureCode`/`TabId` de la misma
lista de columnas SÍ se ven. No se aisló por qué justo `HttpAlias` no.

**9. Lectura en UDF escalar no propagada al llamante** — `dbo.GetUsersAdvancedSearch`, columna `portalid` (dbo.ProfilePropertyDefinition):
```sql
DECLARE @pivotSql nvarchar(max) SELECT @pivotSql = dbo.GetProfileFieldSql(@PortalID, '')
```
(la lectura ocurre dentro del cuerpo de `GetProfileFieldSql`; el oráculo se la atribuye al llamante.)

**10. OUTPUT inserted.col** — `dbo.AddRedirectMessage`, columna `messageid` (dbo.RedirectMessages):
```sql
INSERT INTO dbo.RedirectMessages (UserId, TabId, MessageText)
OUTPUT inserted.MessageId
VALUES(@UserId, @TabId, @Text)
```

**11. UPDATE ... SET @var = columna** — `dbo.AddFile`, columna `fileid` (dbo.Files):
```sql
UPDATE dbo.[Files]
SET /* retrieves FileId from table */ @FileID = FileId, FileName = @FileName, ...
```

## Comprobaciones de instrumento (regla del cero culpable)

- Ninguna causa salió en 0; la más pequeña (10 y 11) tiene 1 caso cada una, verificado leyendo el SQL real, no inventado para rellenar.
- Las 140 filas suman exactamente el total medido (sin residuo, sin duplicados, sin filas fantasma) — comprobado por script con verificación de conjuntos disjuntos.
- El recall laxo medido (98,0827 %) es coherente con el suelo publicado en `eval/corpora.json` (0,9808, declarado como truncado por debajo del valor medido): 140/7302 ciegas da 98,08 % exacto.
- Se corrió el gate real (`ColumnRecallGateTests`, 8/8 en verde) con el mismo build, para confirmar que el programa ad hoc no diverge del comportamiento oficial.
- La comparación es sensible a mayúsculas/corchetes por construcción: se reutilizó literalmente `Plain()` (`.Replace("[","").Replace("]","").ToLowerInvariant()`) de `ColumnRecallGateTests.cs`, no una reimplementación propia.

## WWI-DW — solo conteo (sin clasificar por causa, falta de tiempo)

```
oráculo (bruto)        = 627
oráculo laxo (mod,col) = 364
grafo (bruto)          = 480
grafo laxo (mod,col)   = 310
recall laxo            = 84,8901 %   (suelo del manifiesto: 84,80 %)
BLIND (laxo)           = 55
```

Coherente con el suelo publicado (0,848). No se clasificaron las 55 por causa
raíz — ver `followups`.

## Qué atacar primero

La causa #1 (subconsultas anidadas en predicados/expresiones) es el 41 % del
ciego y, a juzgar por los ejemplos, es un único punto de extensión: el
visitor de columnas no desciende dentro de `EXISTS`/`IN`/subconsultas
escalares. MERGE (16 %) es el segundo candidato, igual de acotado (un solo
tipo de sentencia). Juntas explican el 58 % del ciego con dos cambios
localizados, sin tocar el resto del motor.
