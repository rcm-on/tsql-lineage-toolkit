-- Multi-statement TVF (RETURNS @t TABLE(...) declared): output columns come from the
-- declared table, not from analyzing the INSERT body. Mirrors dbo.fn_ParseVersion in the
-- real DNN corpus (blind-refs.md causa #3).
CREATE FUNCTION dbo.fn_ParseVersion (@Version nvarchar(20))
RETURNS @VersionParts TABLE (Major int, Minor int, Build int)
AS
BEGIN
    INSERT INTO @VersionParts (Major, Minor, Build)
    VALUES (1, 2, 3)
    RETURN
END
