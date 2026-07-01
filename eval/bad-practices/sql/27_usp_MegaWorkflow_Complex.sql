-- REGLAS OBJETIVO:
--   "Complejidad alta"          (med, Mantenibilidad) -- cc >= 10
--   "Anidación profunda"        (med, Mantenibilidad) -- >= 4 niveles de IF anidados
--   "Objeto hace demasiado"     (med, Diseño)         -- escribe en >= 5 tablas distintas
--   "Escritura sin protección"  (med, Robustez)       -- sin transacción ni TRY/CATCH
-- "God procedure": demasiada lógica y demasiadas responsabilidades en un solo objeto.
CREATE PROCEDURE dbo.usp_MegaWorkflow_Complex
    @OrderId   INT,
    @Mode      INT,
    @Region    VARCHAR(20),
    @Priority  INT
AS
BEGIN
    DECLARE @Amount DECIMAL(18,2);

    SELECT @Amount = Amount FROM dbo.Orders WHERE OrderId = @OrderId;

    IF @Mode = 1                                    -- nivel 1
    BEGIN
        IF @Region = 'EU'                           -- nivel 2
        BEGIN
            IF @Priority > 5                        -- nivel 3
            BEGIN
                IF @Amount > 10000                  -- nivel 4 (anidación profunda)
                BEGIN
                    UPDATE dbo.Orders SET Status = 'VIP' WHERE OrderId = @OrderId;
                END
                ELSE IF @Amount > 1000              -- decisión extra
                BEGIN
                    UPDATE dbo.Orders SET Status = 'PRIORITY' WHERE OrderId = @OrderId;
                END
            END
        END
        ELSE IF @Region = 'US'                      -- decisión extra
        BEGIN
            INSERT INTO dbo.Shipments (OrderId, Carrier) VALUES (@OrderId, 'FEDEX');
        END
    END
    ELSE IF @Mode = 2                               -- decisión extra
    BEGIN
        INSERT INTO dbo.Inventory (OrderId, Reserved) VALUES (@OrderId, 1);
    END
    ELSE IF @Mode = 3                               -- decisión extra
    BEGIN
        INSERT INTO dbo.Notifications (OrderId, Channel) VALUES (@OrderId, 'EMAIL');
    END

    IF @Priority IS NULL                            -- decisión extra
    BEGIN
        INSERT INTO dbo.OrderAudit (OrderId, Action, LoggedAt) VALUES (@OrderId, N'NO_PRIORITY', GETDATE());
    END

    WHILE @Amount > 0                               -- decisión extra (bucle)
    BEGIN
        SET @Amount = @Amount - 1000;
        IF @Amount < 0                              -- decisión extra
            SET @Amount = 0;
    END
END
