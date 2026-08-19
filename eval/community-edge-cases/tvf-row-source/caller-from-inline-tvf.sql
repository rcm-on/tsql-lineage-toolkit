-- "FROM func(...) AS alias", inline TVF, qualified alias.col referenced in WHERE.
CREATE PROCEDURE dbo.CountActiveRoleForUser
    @PortalId int,
    @RoleId int
AS
BEGIN
    SELECT COUNT(*) AS Total
    FROM dbo.fn_ActiveRoleIds(@PortalId) r
    WHERE r.Item = @RoleId
END
