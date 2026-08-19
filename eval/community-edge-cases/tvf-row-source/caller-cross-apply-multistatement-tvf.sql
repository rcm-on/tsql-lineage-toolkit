-- "CROSS APPLY func(...) AS alias", multi-statement TVF, qualified alias.col referenced
-- in the SELECT list alongside a real table's columns.
CREATE PROCEDURE dbo.ListAssemblyVersions
AS
BEGIN
    SELECT a.AssemblyName, v.Major, v.Minor, v.Build
    FROM dbo.Assemblies a
    CROSS APPLY dbo.fn_ParseVersion(a.Version) AS v
END
