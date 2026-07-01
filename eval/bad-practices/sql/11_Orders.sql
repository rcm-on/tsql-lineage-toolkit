-- HOT TABLE: PK presente, pero escrita por >=4 objetos distintos.
-- Esperado (tabla): "Acoplamiento alto (tabla)" (med, Diseño).
-- NO debe disparar "sin PK" ni "escrita pero nunca leída" (tiene PK y lectores).
CREATE TABLE dbo.Orders
(
    OrderId    INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CustomerId INT               NOT NULL,
    Amount     DECIMAL(18,2)     NOT NULL,
    Status     VARCHAR(20)       NOT NULL,
    OrderDate  DATETIME2         NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId)
);
