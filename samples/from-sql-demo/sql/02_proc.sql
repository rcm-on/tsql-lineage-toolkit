CREATE PROCEDURE dbo.usp_GetCustomerEmail
    @CustomerId INT
AS
BEGIN
    SELECT Email
    FROM dbo.Customers
    WHERE CustomerId = @CustomerId;
END
