SELECT s.CustomerID, pp.FullName AS PrimaryContact FROM dbo.Customers s JOIN dbo.People pp ON s.PrimaryContactPersonID = pp.PersonID
