CREATE TABLE [Production].[ProductSubcategory] (

  [ProductSubcategoryID] int NOT NULL,
  [ProductCategoryID] int NOT NULL,
  [Name] Name NOT NULL,
  [rowguid] uniqueidentifier NOT NULL,
  [ModifiedDate] datetime NOT NULL
);
