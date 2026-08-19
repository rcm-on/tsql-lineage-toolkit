-- Multi-statement TVF, same shape as dbo.SplitStrings_CTE in the real DNN corpus (the
-- exact example quoted in blind-refs.md causa #3).
CREATE FUNCTION dbo.SplitStrings_CTE (@List nvarchar(max), @Delimiter char(1))
RETURNS @Items TABLE (Item nvarchar(4000))
AS
BEGIN
    INSERT INTO @Items (Item)
    VALUES (@List)
    RETURN
END
