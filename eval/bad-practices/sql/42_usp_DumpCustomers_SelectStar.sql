-- REGLA OBJETIVO: "SELECT *" (low, Rendimiento).
-- Solo lectura: aísla el hallazgo de SELECT * (trae columnas de más, frágil ante
-- cambios de esquema, impide cubrir la consulta con índices).
CREATE PROCEDURE dbo.usp_DumpCustomers_SelectStar
AS
BEGIN
    SELECT * FROM dbo.Customers;
END
