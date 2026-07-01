CREATE TABLE [Purchasing].[ShipMethod] (

  [ShipMethodID] int NOT NULL,
  [Name] Name NOT NULL,
  [ShipBase] money NOT NULL,
  [ShipRate] money NOT NULL,
  [rowguid] uniqueidentifier NOT NULL,
  [ModifiedDate] datetime NOT NULL
);
