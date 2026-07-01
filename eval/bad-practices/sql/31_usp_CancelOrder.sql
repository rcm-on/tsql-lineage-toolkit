-- REGLA OBJETIVO: "Escritura sin protección" (med, Robustez).
-- Quinto escritor de dbo.Orders: confirma el "Acoplamiento alto (tabla)".
CREATE PROCEDURE dbo.usp_CancelOrder
    @OrderId INT
AS
BEGIN
    UPDATE dbo.Orders SET Status = 'CANCELLED' WHERE OrderId = @OrderId;
END
