-- Base tables for the test
CREATE TABLE dbo.Customers (
    CustomerID INT PRIMARY KEY,
    CustomerName NVARCHAR(100),
    LastEditedBy INT
);

CREATE TABLE dbo.Customers_Audit (
    AuditID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID INT,
    OldCustomerName NVARCHAR(100),
    ModifiedBy INT,
    ModifiedDate DATETIME
);
GO

-- Procedure that creates the trigger dynamically
CREATE PROCEDURE dbo.usp_SetupTriggers
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX);
    DECLARE @triggerName NVARCHAR(128) = N'TR_Audit_Customers';
    DECLARE @tableName NVARCHAR(128) = N'Customers';
    DECLARE @schemaName NVARCHAR(128) = N'dbo';

    -- Create the new trigger
    SET @sql = N'
        CREATE TRIGGER ' + QUOTENAME(@triggerName) + ' ON ' + QUOTENAME(@schemaName) + '.' + QUOTENAME(@tableName) + '
        AFTER UPDATE
        AS
        BEGIN
            INSERT INTO dbo.Customers_Audit (CustomerID, OldCustomerName, ModifiedBy, ModifiedDate)
            SELECT d.CustomerID, d.CustomerName, i.LastEditedBy, GETDATE()
            FROM deleted d JOIN inserted i ON d.CustomerID = i.CustomerID;
        END;';
    EXEC sp_executesql @sql;
END;
GO