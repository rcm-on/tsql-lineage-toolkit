-- Ground-truth de lineage de vistas, calculado por el PROPIO SQL Server (oráculo
-- autoritativo, maneja todo el T-SQL: CROSS/OUTER APPLY, PIVOT, métodos XML, UNION...).
-- Para cada vista:
--   out_cols  = columnas de SALIDA reales            (sys.columns)         -> deberían ser :Column nodos del toolkit
--   src_cols  = columnas FUENTE referenciadas         (dm_sql_referenced_entities, minor_id>0)
--               == el toolkit debe igualar con READS_COLUMN + FILTERS_ON
--   src_tables= tablas base referenciadas             (== READS_FROM)
--
-- Uso:  sqlcmd -S localhost\SQLEXPRESS -E -C -d <DB> -h-1 -s"," -W -i extract-truth.sql
SET NOCOUNT ON;
DECLARE @v NVARCHAR(300);
DECLARE c CURSOR FOR
  SELECT s.name + '.' + o.name
  FROM sys.views o JOIN sys.schemas s ON s.schema_id = o.schema_id
  ORDER BY 1;
OPEN c; FETCH NEXT FROM c INTO @v;
WHILE @@FETCH_STATUS = 0
BEGIN
  DECLARE @outc INT = (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@v));
  DECLARE @srcc INT = 0, @tbls INT = 0;
  BEGIN TRY
    SELECT @srcc = COUNT(DISTINCT referenced_schema_name + '.' + referenced_entity_name + '.' + referenced_minor_name),
           @tbls = COUNT(DISTINCT referenced_schema_name + '.' + referenced_entity_name)
    FROM sys.dm_sql_referenced_entities(@v, 'OBJECT')
    WHERE referenced_minor_id > 0;
  END TRY BEGIN CATCH SET @srcc = -1; END CATCH;
  PRINT DB_NAME() + ',' + @v + ',' + CAST(@outc AS VARCHAR) + ',' + CAST(@srcc AS VARCHAR) + ',' + CAST(@tbls AS VARCHAR);
  FETCH NEXT FROM c INTO @v;
END
CLOSE c; DEALLOCATE c;
