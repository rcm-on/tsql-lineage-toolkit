-- REGLA OBJETIVO: "SQL dinámico" (high, Seguridad).
-- Construye y ejecuta SQL dinámico a partir de PARÁMETROS (no de datos de tabla),
-- por lo que es SQL dinámico de riesgo pero NO se clasifica como inyección desde datos.
CREATE PROCEDURE dbo.usp_DynamicReport
    @TableName SYSNAME,
    @Filter    NVARCHAR(200)
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX);
    SET @sql = N'SELECT * FROM ' + @TableName + N' WHERE ' + @Filter;
    EXEC (@sql);
END
