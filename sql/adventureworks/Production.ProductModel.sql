CREATE TABLE [Production].[ProductModel] (

  [ProductModelID] int NOT NULL,
  [Name] Name NOT NULL,
  [CatalogDescription] xml NULL,
  [Instructions] xml NULL,
  [rowguid] uniqueidentifier NOT NULL,
  [ModifiedDate] datetime NOT NULL
);
