-- REGLA OBJETIVO: "Escritura sin protección" (med, Robustez).
-- Además, junto con los demás escritores de dbo.Orders, contribuye al
-- "Acoplamiento alto (tabla)" sobre dbo.Orders.
CREATE PROCEDURE dbo.usp_CreateOrder
    @CustomerId INT,
    @Amount     DECIMAL(18,2)
AS
BEGIN
    INSERT INTO dbo.Orders (CustomerId, Amount, Status, OrderDate)
    VALUES (@CustomerId, @Amount, 'NEW', GETDATE());
END
