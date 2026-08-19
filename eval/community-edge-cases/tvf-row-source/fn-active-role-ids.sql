-- Inline TVF (RETURN (SELECT ...)): output columns come from the SELECT list, not from a
-- declared table.
CREATE FUNCTION dbo.fn_ActiveRoleIds (@PortalId int)
RETURNS TABLE
AS
RETURN
(
    SELECT ur.RoleID AS Item
    FROM dbo.UserRoles ur
    WHERE ur.PortalId = @PortalId
)
