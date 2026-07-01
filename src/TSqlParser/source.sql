-- Test case for advanced DML: MERGE with an OUTPUT clause.
-- This is the first test case for Task F (Gap Hunting).

-- 1. Source of changes
CREATE TABLE dbo.SourceProducts (
    ProductID INT PRIMARY KEY,
    ProductName NVARCHAR(100),
    Price DECIMAL(18, 2)
);

-- 2. Target table to be synchronized
CREATE TABLE dbo.TargetProducts (
    ProductID INT PRIMARY KEY,
    ProductName NVARCHAR(100),
    Price DECIMAL(18, 2),
    LastModified DATETIME2
);

-- 3. Log table for auditing changes
CREATE TABLE dbo.ProductMergeLog (
    LogID INT IDENTITY PRIMARY KEY,
    ActionType NVARCHAR(10),
    InsertedID INT,
    DeletedID INT,
    NewPrice DECIMAL(18, 2),
    OldPrice DECIMAL(18, 2)
);

-- 4. Procedure containing the MERGE statement for analysis
CREATE PROCEDURE dbo.usp_SyncProducts
AS
BEGIN
    MERGE INTO dbo.TargetProducts AS T
    USING dbo.SourceProducts AS S
    ON T.ProductID = S.ProductID
    WHEN MATCHED THEN
        UPDATE SET T.Price = S.Price, T.LastModified = GETUTCDATE()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductID, ProductName, Price, LastModified)
        VALUES (S.ProductID, S.ProductName, S.Price, GETUTCDATE())
    WHEN NOT MATCHED BY SOURCE THEN
        DELETE
    OUTPUT $action, inserted.ProductID, deleted.ProductID, inserted.Price, deleted.Price
    INTO dbo.ProductMergeLog (ActionType, InsertedID, DeletedID, NewPrice, OldPrice);
END;