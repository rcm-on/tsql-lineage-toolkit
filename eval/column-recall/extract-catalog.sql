-- Genera el ground-truth de lineage de columna: para cada modulo de la base,
-- que columna de que entidad lee, segun el propio resolvedor de dependencias de
-- SQL Server. Una fila por tripleta: modulo|entidad|columna, en minusculas.
--
-- Uso:
--   sqlcmd -S localhost\SQLEXPRESS -E -C -d DnnCorpus -h-1 -W -i extract-catalog.sql ^
--     | findstr "|" > catalog-columns.psv
--
-- El cursor es necesario porque dm_sql_referenced_entities es una funcion con
-- parametro de objeto: no se puede aplicar en bloque sobre sys.objects. El
-- TRY/CATCH cubre los modulos cuyas dependencias SQL Server no puede resolver
-- (referencias a objetos inexistentes); se cuentan aparte al final.
SET NOCOUNT ON;

CREATE TABLE #refs (
    module nvarchar(300),
    entity nvarchar(300),
    col    nvarchar(300)
);

DECLARE @name nvarchar(300), @failed int = 0;

DECLARE modules CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name)
    FROM sys.objects
    WHERE is_ms_shipped = 0
      AND type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR');

OPEN modules;
FETCH NEXT FROM modules INTO @name;

WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        INSERT #refs (module, entity, col)
        SELECT
            @name,
            ISNULL(referenced_schema_name, 'dbo') + '.' + referenced_entity_name,
            referenced_minor_name
        FROM sys.dm_sql_referenced_entities(@name, 'OBJECT')
        WHERE referenced_minor_id > 0          -- > 0 = referencia a COLUMNA, no al objeto entero
          AND referenced_minor_name IS NOT NULL
          AND referenced_id IS NOT NULL;       -- descarta lo que SQL Server no resuelve
    END TRY
    BEGIN CATCH
        SET @failed = @failed + 1;
    END CATCH;

    FETCH NEXT FROM modules INTO @name;
END

CLOSE modules;
DEALLOCATE modules;

-- COLLATE DATABASE_DEFAULT: los nombres de sys.* llevan la intercalacion del
-- catalogo y concatenarlos con literales revienta si la base usa otra.
SELECT DISTINCT
    LOWER(REPLACE(REPLACE(module, '[', ''), ']', '') + '|' + entity + '|' + col)
        COLLATE DATABASE_DEFAULT
FROM #refs
ORDER BY 1;

PRINT '-- modulos cuyas dependencias no se pudieron resolver: ' + CAST(@failed AS varchar(10));

DROP TABLE #refs;
