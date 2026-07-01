-- Caso: funciones de ventana (OVER ... PARTITION BY ... ORDER BY ...).
-- NO es un gap (falso positivo del informe inicial). Sirve de GUARDA DE REGRESIÓN: el
-- lineage SÍ debe extraerse. Esperado: RunningTotal DERIVES_FROM Amount, CustomerID y
-- OrderDate (columnas dentro del OVER).
CREATE VIEW dbo.vRunningTotal AS
SELECT CustomerID, OrderDate, Amount,
       SUM(Amount) OVER (PARTITION BY CustomerID ORDER BY OrderDate ROWS UNBOUNDED PRECEDING) AS RunningTotal
FROM dbo.Sales;
