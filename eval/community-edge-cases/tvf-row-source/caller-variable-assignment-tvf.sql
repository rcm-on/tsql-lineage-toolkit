-- "SELECT @v1 = Col1, @v2 = Col2 FROM func(...)" (no alias, unqualified, single TVF
-- source) - same shape as dbo.fn_CompareVersion in the real DNN corpus. Also a caller
-- object that is itself a scalar function (RETURNS int), not a procedure.
CREATE FUNCTION dbo.fn_CompareVersion (@Version nvarchar(20), @CurrentVersion nvarchar(20))
RETURNS int
AS
BEGIN
    DECLARE @MajorVersion int
    DECLARE @MinorVersion int
    DECLARE @BuildVersion int

    SELECT @MajorVersion = Major, @MinorVersion = Minor, @BuildVersion = Build
    FROM dbo.fn_ParseVersion(@Version)

    RETURN 0
END
