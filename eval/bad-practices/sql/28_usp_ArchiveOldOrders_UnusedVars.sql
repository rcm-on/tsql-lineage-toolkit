-- REGLAS OBJETIVO:
--   "Variable sin uso"          (low, Mantenibilidad) -- @NeverUsed, @AlsoUnused
--   "Escritura sin protección"  (med, Robustez)       -- DELETE sin transacción/errores
CREATE PROCEDURE dbo.usp_ArchiveOldOrders_UnusedVars
    @CutoffDate DATE
AS
BEGIN
    DECLARE @NeverUsed  INT;            -- declarada y nunca usada
    DECLARE @AlsoUnused VARCHAR(50);    -- declarada y nunca usada

    DELETE FROM dbo.Orders WHERE OrderDate < @CutoffDate;
END
