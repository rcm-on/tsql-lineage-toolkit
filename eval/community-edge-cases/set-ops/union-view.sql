-- Caso: vista con UNION (set operation) — el cuerpo es un BinaryQueryExpression.
-- Gap original: ViewColumnLineage solo manejaba un QuerySpecification -> 0 columnas.
-- Tras el fix (QuerySpecs recorre todas las ramas): la columna de salida (nombre de la 1ª
-- rama) DERIVES_FROM la columna posicionalmente equivalente de CADA rama.
-- Esperado: a <- dbo.t1.a  Y  a <- dbo.t2.b.
CREATE VIEW dbo.vUnion AS
SELECT a FROM dbo.t1
UNION
SELECT b FROM dbo.t2;
