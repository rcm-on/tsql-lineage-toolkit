-- REGLAS OBJETIVO:
--   "UPDATE/DELETE sin WHERE" (high, Integridad) -- DELETE y UPDATE sin filtro
--   "Escritura sin protección" (med, Robustez)   -- sin transacción ni TRY/CATCH
-- Borra/actualiza TODAS las filas de la tabla: un parámetro olvidado y se va la tabla.
CREATE PROCEDURE dbo.usp_PurgeAll_NoWhere
AS
BEGIN
    DELETE FROM dbo.Orders;
    UPDATE dbo.Orders SET Status = 'RESET';
END
