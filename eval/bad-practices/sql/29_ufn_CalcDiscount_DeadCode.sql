-- REGLA OBJETIVO: "Posible código muerto" (low, Mantenibilidad).
-- Función (prefijo ufn) sin llamadores detectados y sin escrituras: o se usa en
-- vistas/columnas calculadas no analizadas, o está obsoleta.
CREATE FUNCTION dbo.ufn_CalcDiscount (@CustomerId INT)
RETURNS DECIMAL(18,2)
AS
BEGIN
    DECLARE @r DECIMAL(18,2);
    SELECT @r = TotalSpent * 0.1 FROM dbo.Customers WHERE CustomerId = @CustomerId;
    RETURN @r;
END
