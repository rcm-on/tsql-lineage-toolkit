-- REGLA OBJETIVO: "Error de parseo" (info, Mantenibilidad).
-- T-SQL deliberadamente inválido: verifica que el analizador degrada con elegancia
-- (un objeto que no parsea no debe tumbar el resto del corpus).
CREATE PROCEDURE dbo.usp_Broken_ParseError
AS
BEGIN
    SELECT FROM WHERE;
    UPDATE SET = ;
END
