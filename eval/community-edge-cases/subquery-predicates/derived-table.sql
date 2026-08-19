-- Derived table in FROM ("(SELECT ...) alias"): the derived table's own WHERE filters
-- rows before the outer query ever sees them - that predicate belongs to
-- dbo.PortalLocalization, not to the outer alias "portals".
CREATE TABLE dbo.Portals (
    PortalID int PRIMARY KEY,
    PortalName varchar(100),
    DefaultLanguage varchar(10)
);
GO

CREATE TABLE dbo.PortalLocalization (
    PortalID int,
    CultureCode varchar(10),
    PortalName varchar(100)
);
GO

CREATE PROCEDURE dbo.GetLocalizedPortalNames
AS
BEGIN
    SELECT portals.PortalID, portals.PortalName
    FROM (SELECT pl.PortalID, pl.PortalName FROM dbo.PortalLocalization pl WHERE pl.CultureCode = 'en-US') portals
END
