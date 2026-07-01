CREATE TABLE [Purchasing].[Vendor] (

  [BusinessEntityID] int NOT NULL,
  [AccountNumber] AccountNumber NOT NULL,
  [Name] Name NOT NULL,
  [CreditRating] tinyint NOT NULL,
  [PreferredVendorStatus] Flag NOT NULL,
  [ActiveFlag] Flag NOT NULL,
  [PurchasingWebServiceURL] nvarchar(1024) NULL,
  [ModifiedDate] datetime NOT NULL
);
