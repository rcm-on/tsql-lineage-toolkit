-- REGLAS OBJETIVO:
--   "Escrituras repetidas a la misma tabla" (med, Rendimiento) -- 3 INSERT a OrderAudit
--   "Escritura sin protección"              (med, Robustez)    -- sin transacción/errores
-- Candidato a consolidar en una sola operación de conjunto.
CREATE PROCEDURE dbo.usp_LogEverything_RepeatWrites
    @OrderId INT,
    @Msg     NVARCHAR(400)
AS
BEGIN
    INSERT INTO dbo.OrderAudit (OrderId, Action, LoggedAt) VALUES (@OrderId, N'START', GETDATE());
    INSERT INTO dbo.OrderAudit (OrderId, Action, LoggedAt) VALUES (@OrderId, @Msg,     GETDATE());
    INSERT INTO dbo.OrderAudit (OrderId, Action, LoggedAt) VALUES (@OrderId, N'END',   GETDATE());
END
