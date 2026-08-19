-- Scalar comparison "col = (SELECT ...)": the unqualified "PortalID" exists in both the
-- outer table and the subquery's own table.
CREATE TABLE dbo.Files (
    FileID int PRIMARY KEY,
    PortalID int,
    FileName varchar(260)
);
GO

CREATE TABLE dbo.Portals (
    PortalID int PRIMARY KEY,
    DefaultLanguage varchar(10)
);
GO

CREATE PROCEDURE dbo.GetDefaultLanguageFiles
AS
BEGIN
    SELECT FileID FROM dbo.Files
    WHERE PortalID = (SELECT PortalID FROM dbo.Portals WHERE DefaultLanguage = 'en-US')
END
