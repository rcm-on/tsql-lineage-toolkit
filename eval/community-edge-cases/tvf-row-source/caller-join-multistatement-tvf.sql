-- "JOIN func(...) AS alias ON ... = alias.col", multi-statement TVF, inside an
-- INSERT...SELECT. Same shape as dbo.CoreMessaging_CreateMessageRecipientsForRole in the
-- real DNN corpus (blind-refs.md causa #3 worked example).
CREATE PROCEDURE dbo.CreateMessageRecipientsForRole
    @MessageID int,
    @RoleIDs nvarchar(max)
AS
BEGIN
    INSERT dbo.MessageRecipients (MessageID, UserID)
    SELECT DISTINCT @MessageID, ur.UserID
    FROM dbo.UserRoles ur
    INNER JOIN dbo.SplitStrings_CTE(@RoleIDs, ',') m ON ur.RoleID = m.Item
END
