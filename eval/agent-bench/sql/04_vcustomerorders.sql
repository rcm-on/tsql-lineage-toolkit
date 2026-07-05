CREATE VIEW dbo.vCustomerOrders AS SELECT c.Name AS CustomerName, o.Total AS OrderTotal FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.Id;
