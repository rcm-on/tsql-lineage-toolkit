-- Nivel 2: vista sobre vista (lee de vOrdersEnriched, no de la tabla base).
CREATE VIEW dbo.vOrdersSummary AS
SELECT
    OrderID,
    NetAmount AS TotalAmount,
    OrderDate
FROM dbo.vOrdersEnriched;
