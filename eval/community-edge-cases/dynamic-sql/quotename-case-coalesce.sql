-- Repro minima del patron real de WideWorldImporters
-- (DataLoadSimulation.DeactivateTemporalTablesBeforeDataLoad): variables
-- literales (SET @x = N'...') usadas dentro de SQL dinamico via QUOTENAME(),
-- CASE WHEN y COALESCE.
--
-- Paso 1 (YA RESUELTO, ver AstWalker.ResolveLiteral, caso FunctionCall
-- QUOTENAME): un QUOTENAME(@var) simple dentro de la concatenacion debe dar
-- texto literal completo, no "".
--
-- Paso 2 (RESUELTO, gap 5.2): la misma concatenacion pero con un
-- "CASE WHEN <comparacion booleana> THEN <texto> ELSE <texto> END" en medio.
-- Ahora resuelve a "SELECT [LastEditedBy] FROM [Orders];". Lo que lo arreglo en
-- AstWalker.ResolveLiteral (ver docs/extraction-gaps.md SS5.2):
--   1. CoalesceExpression (COALESCE NO es un FunctionCall en ScriptDom, es su
--      propio nodo): devolver el primer Expressions[i] que resuelva.
--   2. SearchedCaseExpression (CASE WHEN/THEN/ELSE): evaluar cada WhenClause via
--      un evaluador booleano estatico nuevo, ResolveBoolean(BooleanExpression),
--      que cubre BooleanComparisonExpression (=/<>), IS [NOT] NULL, parentesis y
--      AND/OR. Falla cerrado ante comparaciones de orden u operandos no resolubles.
-- (El tercer bloqueador del caso real de WWI, NCHAR(n) en @CrLf, no aparece aqui
-- porque esta repro no usa @CrLf; se cubre en el procedimiento real, ver SS5.2.)
CREATE PROCEDURE dbo.usp_BuildDynamicTriggerName
AS
BEGIN
    DECLARE @SQL nvarchar(max);
    DECLARE @SchemaName sysname = N'dbo';
    DECLARE @TableName sysname = N'Orders';
    DECLARE @LastEditedByColumnName sysname = N'LastEditedBy';

    -- Paso 1: solo QUOTENAME(@var) en la concatenacion -> debe resolverse.
    SET @SQL = N'DROP TRIGGER IF EXISTS ' + QUOTENAME(@SchemaName) + N'.[TR_Test];';
    EXEC (@SQL);

    -- Paso 2: CASE WHEN COALESCE(...) <> N'' THEN QUOTENAME(...) ELSE N'NULL' END
    -- en medio de la concatenacion -> resuelve a "SELECT [LastEditedBy] FROM [Orders];".
    SET @SQL = N'SELECT '
        + CASE WHEN COALESCE(@LastEditedByColumnName, N'') <> N'' THEN QUOTENAME(@LastEditedByColumnName) ELSE N'NULL' END
        + N' FROM ' + QUOTENAME(@TableName) + N';';
    EXEC (@SQL);
END
