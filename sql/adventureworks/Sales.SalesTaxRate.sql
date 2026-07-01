CREATE TABLE [Sales].[SalesTaxRate] (

  [SalesTaxRateID] int NOT NULL,
  [StateProvinceID] int NOT NULL,
  [TaxType] tinyint NOT NULL,
  [TaxRate] smallmoney NOT NULL,
  [Name] Name NOT NULL,
  [rowguid] uniqueidentifier NOT NULL,
  [ModifiedDate] datetime NOT NULL
);
