-- CASO PR-IMPACT (demo de la Action): proc nuevo que ESCRIBE dbo.Customers.
-- Esperado en el diff del PR: objeto añadido cuyas escrituras alcanzan
-- dbo.Customers, con sus lectores aguas abajo como nuevos afectados (via_data).
CREATE PROCEDURE dbo.usp_RecalculateCustomerSpend
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE c
    SET    c.TotalSpent = ISNULL(o.Total, 0)
    FROM   dbo.Customers AS c
    LEFT JOIN (
        SELECT CustomerId, SUM(Amount) AS Total
        FROM   dbo.Orders
        GROUP BY CustomerId
    ) AS o ON o.CustomerId = c.CustomerId;
END;
