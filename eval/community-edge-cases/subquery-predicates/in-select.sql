-- IN (SELECT ...) predicate with an unqualified column name that collides between the
-- outer table and the subquery's own table ("ModuleDefID" exists in both). Mirrors the
-- real DNN Platform blind ref: dbo.DeleteDesktopModule / dbo.ModuleDefinitions. ModuleDefID.
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

CREATE PROCEDURE dbo.DeleteDesktopModule
    @DesktopModuleId int
AS
BEGIN
    DELETE FROM dbo.Permission
    WHERE ModuleDefID IN (SELECT ModuleDefID FROM dbo.ModuleDefinitions WHERE DesktopModuleID = @DesktopModuleId)
END
