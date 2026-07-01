-- Nivel 1: lee directo de la tabla base.
CREATE VIEW dbo.vOrdersEnriched AS
SELECT
    OrderID,
    CustomerID,
    Amount AS NetAmount,
    OrderDate
FROM dbo.Orders;
