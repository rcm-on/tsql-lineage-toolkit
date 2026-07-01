-- REGLA OBJETIVO: "Prefijo sp_" (med, Rendimiento).
-- Solo lectura (sin escrituras): aísla el hallazgo del prefijo sp_, que obliga a
-- SQL Server a buscar primero en master y puede colisionar con procs de sistema.
CREATE PROCEDURE dbo.sp_GetActiveCustomers
AS
BEGIN
    SELECT c.CustomerId, c.CustomerName, o.OrderId
    FROM dbo.Customers AS c
    JOIN dbo.Orders    AS o ON o.CustomerId = c.CustomerId
    WHERE c.IsActive = 1;
END
