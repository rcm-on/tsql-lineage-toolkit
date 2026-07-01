-- REGLAS OBJETIVO:
--   "Cursor en transacción sin TRY/CATCH" (high, Robustez)
--   "Uso de cursor"                        (med, Rendimiento)
-- Cursor + transacción abierta y SIN manejo de errores: si algo falla dentro del
-- bucle, la transacción y el cursor quedan huérfanos.
CREATE PROCEDURE dbo.usp_ProcessQueue_CursorTx
AS
BEGIN
    DECLARE @id INT, @amt DECIMAL(18,2);

    BEGIN TRANSACTION;

    DECLARE c CURSOR FOR
        SELECT OrderId, Amount FROM dbo.Orders WHERE Status = 'NEW';

    OPEN c;
    FETCH NEXT FROM c INTO @id, @amt;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        UPDATE dbo.Orders SET Status = 'PROCESSING' WHERE OrderId = @id;
        FETCH NEXT FROM c INTO @id, @amt;
    END

    CLOSE c;
    DEALLOCATE c;

    COMMIT TRANSACTION;
END
