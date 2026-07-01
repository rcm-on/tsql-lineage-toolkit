CREATE TABLE [Production].[ProductPhoto] (

  [ProductPhotoID] int NOT NULL,
  [ThumbNailPhoto] varbinary(max) NULL,
  [ThumbnailPhotoFileName] nvarchar(50) NULL,
  [LargePhoto] varbinary(max) NULL,
  [LargePhotoFileName] nvarchar(50) NULL,
  [ModifiedDate] datetime NOT NULL
);
