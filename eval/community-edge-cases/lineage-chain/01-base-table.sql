-- Caso: cadena de lineage de columna a través de varias vistas apiladas
-- (vista sobre vista sobre vista), análogo a la cadena CALLS de 4 niveles
-- usada en docs/nodestore-analysis.md (Caso 4/6), pero para DERIVES_FROM.
-- Ni WideWorldImporters ni AdventureWorks tienen en este corpus vistas que
-- referencien a otras vistas (las 23 procesadas derivan directo de tablas
-- base, profundidad 1) — este caso se construye a propósito para poder medir.
CREATE TABLE dbo.Orders (
    OrderID INT NOT NULL,
    CustomerID INT NOT NULL,
    Amount DECIMAL(10,2) NOT NULL,
    OrderDate DATE NOT NULL
);
