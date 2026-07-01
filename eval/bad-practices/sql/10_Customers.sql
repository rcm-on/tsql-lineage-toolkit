-- CONTROL (tabla limpia): PK presente, leída por varios objetos.
-- Esperado: NINGÚN hallazgo (sirve para verificar que el analizador no genera falsos positivos).
CREATE TABLE dbo.Customers
(
    CustomerId   INT           NOT NULL PRIMARY KEY,
    CustomerName NVARCHAR(200) NOT NULL,
    IsActive     BIT           NOT NULL,
    TotalSpent   DECIMAL(18,2) NOT NULL
);
