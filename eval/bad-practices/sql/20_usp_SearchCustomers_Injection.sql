-- REGLA OBJETIVO: "Inyección SQL" (crit, Seguridad).
-- @sql se ASIGNA desde una columna de tabla (SearchConfig.DefaultSort) y luego
-- CONSTRUYE el SQL dinámico ejecutado: dato de tabla -> string ejecutable.
-- (Por la rama if/else del rule engine, este objeto NO recibe además "SQL dinámico".)
CREATE PROCEDURE dbo.usp_SearchCustomers_Injection
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX);

    SELECT @sql = N'SELECT CustomerId, CustomerName FROM dbo.Customers ORDER BY ' + DefaultSort
    FROM dbo.SearchConfig
    WHERE ConfigKey = 'CustomerSearch';

    EXEC (@sql);
END
