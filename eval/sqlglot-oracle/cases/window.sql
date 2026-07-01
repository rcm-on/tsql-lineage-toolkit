SELECT CustomerID, SUM(Amount) OVER (PARTITION BY CustomerID ORDER BY OrderDate) AS rt FROM dbo.Sales
