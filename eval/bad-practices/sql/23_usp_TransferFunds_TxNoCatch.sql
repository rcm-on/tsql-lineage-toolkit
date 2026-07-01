-- REGLA OBJETIVO: "Transacción sin TRY/CATCH" (high, Robustez).
-- Dos UPDATE dentro de una transacción sin BEGIN TRY/CATCH: si el segundo falla,
-- la transacción queda abierta (riesgo de bloqueo / inconsistencia).
CREATE PROCEDURE dbo.usp_TransferFunds_TxNoCatch
    @FromId INT,
    @ToId   INT,
    @Amount DECIMAL(18,2)
AS
BEGIN
    BEGIN TRANSACTION;

    UPDATE dbo.Orders SET Amount = Amount - @Amount WHERE OrderId = @FromId;
    UPDATE dbo.Orders SET Amount = Amount + @Amount WHERE OrderId = @ToId;

    COMMIT TRANSACTION;
END
