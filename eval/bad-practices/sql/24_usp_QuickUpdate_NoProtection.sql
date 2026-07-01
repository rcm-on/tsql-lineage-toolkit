-- REGLA OBJETIVO: "Escritura sin protección" (med, Robustez).
-- Modifica datos sin transacción ni manejo de errores.
CREATE PROCEDURE dbo.usp_QuickUpdate_NoProtection
    @OrderId INT,
    @Status  VARCHAR(20)
AS
BEGIN
    UPDATE dbo.Orders SET Status = @Status WHERE OrderId = @OrderId;
END
