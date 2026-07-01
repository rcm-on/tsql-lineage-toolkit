-- REGLAS OBJETIVO:
--   "TRUNCATE de tabla"        (med, Integridad) -- borrado masivo, reinicia IDENTITY
--   "Escritura sin protección" (med, Robustez)   -- sin transacción ni TRY/CATCH
CREATE PROCEDURE dbo.usp_TruncateAudit
AS
BEGIN
    TRUNCATE TABLE dbo.OrderAudit;
END
