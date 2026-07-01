CREATE TABLE [Person].[Person] (

  [BusinessEntityID] int NOT NULL,
  [PersonType] nchar(2) NOT NULL,
  [NameStyle] NameStyle NOT NULL,
  [Title] nvarchar(8) NULL,
  [FirstName] Name NOT NULL,
  [MiddleName] Name NULL,
  [LastName] Name NOT NULL,
  [Suffix] nvarchar(10) NULL,
  [EmailPromotion] int NOT NULL,
  [AdditionalContactInfo] xml NULL,
  [Demographics] xml NULL,
  [rowguid] uniqueidentifier NOT NULL,
  [ModifiedDate] datetime NOT NULL
);
