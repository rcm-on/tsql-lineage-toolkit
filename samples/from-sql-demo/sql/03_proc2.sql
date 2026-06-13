CREATE PROCEDURE Sales.usp_UpdateCustomerEmail
    @CustomerId INT,
    @NewEmail NVARCHAR(200)
AS
BEGIN
    UPDATE dbo.Customers
    SET Email = @NewEmail
    WHERE CustomerId = @CustomerId;

    EXEC dbo.usp_GetCustomerEmail @CustomerId;
END
