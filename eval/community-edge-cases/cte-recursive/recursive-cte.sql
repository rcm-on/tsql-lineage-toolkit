-- Caso: vista con CTE RECURSIVA (anchor UNION ALL recursive member).
-- Gap original: 0 extracción (cuerpo BinaryQueryExpression no resolvía base tables) -> la
-- vista era invisible (0 READS_FROM, 0 DERIVES_FROM).
-- Tras el fix (AstWalker.CollectQueryExprTableRefs): resuelve la tabla base por ambas ramas.
-- Esperado: READS_FROM dbo.Employees; DERIVES_FROM EmployeeID<-Employees.EmployeeID,
-- ManagerID<-Employees.ManagerID. Limitación conocida: Lvl (columna calculada de recursión)
-- mapea a un fantasma Employees.Lvl.
CREATE VIEW dbo.vOrgChart AS
WITH cte AS (
  SELECT EmployeeID, ManagerID, 0 AS Lvl
  FROM dbo.Employees WHERE ManagerID IS NULL
  UNION ALL
  SELECT e.EmployeeID, e.ManagerID, c.Lvl + 1
  FROM dbo.Employees e JOIN cte c ON e.ManagerID = c.EmployeeID
)
SELECT EmployeeID, ManagerID, Lvl FROM cte;
