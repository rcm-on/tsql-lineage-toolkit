CREATE TABLE [dbo].[DatabaseLog] (

  [DatabaseLogID] int NOT NULL,
  [PostTime] datetime NOT NULL,
  [DatabaseUser] sysname NOT NULL,
  [Event] sysname NOT NULL,
  [Schema] sysname NULL,
  [Object] sysname NULL,
  [TSQL] nvarchar(max) NOT NULL,
  [XmlEvent] xml NOT NULL
);
