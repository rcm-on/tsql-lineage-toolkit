-- "SELECT * FROM func(...)": star expansion needs the TVF's known output columns
-- (TvfOutputColumns / InputAnalyzer's TVF pre-pass), same mechanism as "SELECT * FROM
-- view".
CREATE PROCEDURE dbo.ShowParsedVersion
    @Version nvarchar(20)
AS
BEGIN
    SELECT * FROM dbo.fn_ParseVersion(@Version)
END
