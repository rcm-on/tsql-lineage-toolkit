-- NOT EXISTS(...) predicate, correlated: the inner unqualified column ("ModuleDefID")
-- collides by name with the outer table's own column, and the correlation itself is a
-- qualified reference back to the outer alias ("Permission.ModuleDefID").
CREATE TABLE dbo.Permission (
    PermissionID int PRIMARY KEY,
    ModuleDefID int,
    PermissionKey varchar(50)
);
GO

CREATE TABLE dbo.ModuleDefinitions (
    ModuleDefID int PRIMARY KEY,
    DesktopModuleID int,
    FriendlyName varchar(50)
);
GO

CREATE PROCEDURE dbo.PurgeOrphanPermissions
AS
BEGIN
    DELETE p FROM dbo.Permission p
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ModuleDefinitions WHERE ModuleDefID = p.ModuleDefID)
END
