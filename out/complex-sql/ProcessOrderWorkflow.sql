CREATE OR ALTER PROCEDURE dbo.ProcessOrderWorkflow
    @OrderID INT,
    @CustomerID INT,
    @ForceReview BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OrderTotal DECIMAL(18,2);
    DECLARE @CustomerCreditLimit DECIMAL(18,2);
    DECLARE @CustomerBalance DECIMAL(18,2);
    DECLARE @StockItemID INT;
    DECLARE @AvailableQty INT;
    DECLARE @RequiredQty INT;
    DECLARE @RiskLevel VARCHAR(20);
    DECLARE @ApprovalStatus VARCHAR(20);
    DECLARE @ErrorMsg NVARCHAR(400);

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Step 1: load order header
        SELECT @OrderTotal = od.OrderTotal,
               @CustomerID = o.CustomerID
        FROM Sales.Orders AS o
        JOIN Sales.OrderLines AS od ON od.OrderID = o.OrderID
        WHERE o.OrderID = @OrderID;

        IF @OrderTotal IS NULL
        BEGIN
            RAISERROR('Order not found', 16, 1);
            RETURN;
        END

        -- Step 2: credit check with nested conditions
        SELECT @CustomerCreditLimit = CreditLimit,
               @CustomerBalance = AccountBalance
        FROM Sales.Customers
        WHERE CustomerID = @CustomerID;

        IF @CustomerBalance + @OrderTotal > @CustomerCreditLimit
        BEGIN
            IF @ForceReview = 1
            BEGIN
                SET @ApprovalStatus = 'PENDING_REVIEW';

                IF @OrderTotal > 10000
                BEGIN
                    SET @RiskLevel = 'HIGH';

                    IF EXISTS (SELECT 1 FROM Sales.CustomerTransactions
                               WHERE CustomerID = @CustomerID AND IsFinalized = 0)
                    BEGIN
                        SET @RiskLevel = 'CRITICAL';
                    END
                    ELSE
                    BEGIN
                        SET @RiskLevel = 'HIGH';
                    END
                END
                ELSE IF @OrderTotal > 1000
                BEGIN
                    SET @RiskLevel = 'MEDIUM';
                END
                ELSE
                BEGIN
                    SET @RiskLevel = 'LOW';
                END

                INSERT INTO Sales.OrderApprovals (OrderID, RiskLevel, Status)
                VALUES (@OrderID, @RiskLevel, @ApprovalStatus);
            END
            ELSE
            BEGIN
                SET @ApprovalStatus = 'REJECTED';

                UPDATE Sales.Orders
                SET OrderStatus = 'REJECTED'
                WHERE OrderID = @OrderID;

                RAISERROR('Order exceeds credit limit', 16, 1);
                RETURN;
            END
        END
        ELSE
        BEGIN
            SET @ApprovalStatus = 'APPROVED';
        END

        -- Step 3: stock allocation loop with nested cursor-like WHILE
        DECLARE @LineCursor CURSOR;
        SET @LineCursor = CURSOR FOR
            SELECT StockItemID, Quantity
            FROM Sales.OrderLines
            WHERE OrderID = @OrderID;

        OPEN @LineCursor;
        FETCH NEXT FROM @LineCursor INTO @StockItemID, @RequiredQty;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @AvailableQty = QuantityOnHand
            FROM Warehouse.StockItemHoldings
            WHERE StockItemID = @StockItemID;

            IF @AvailableQty IS NULL
            BEGIN
                SET @ErrorMsg = 'No stock holding record for item ' + CAST(@StockItemID AS VARCHAR(10));

                IF @ForceReview = 1
                BEGIN
                    INSERT INTO Warehouse.StockItemTransactions (StockItemID, TransactionTypeID, Quantity)
                    VALUES (@StockItemID, 99, 0);
                END
                ELSE
                BEGIN
                    RAISERROR(@ErrorMsg, 16, 1);
                    CLOSE @LineCursor;
                    DEALLOCATE @LineCursor;
                    ROLLBACK TRANSACTION;
                    RETURN;
                END
            END
            ELSE IF @AvailableQty < @RequiredQty
            BEGIN
                IF @ApprovalStatus = 'APPROVED'
                BEGIN
                    -- backorder branch
                    INSERT INTO Warehouse.StockItemTransactions (StockItemID, TransactionTypeID, Quantity)
                    VALUES (@StockItemID, 10, @RequiredQty - @AvailableQty);

                    UPDATE Warehouse.StockItemHoldings
                    SET QuantityOnHand = 0
                    WHERE StockItemID = @StockItemID;
                END
                ELSE
                BEGIN
                    UPDATE Sales.OrderLines
                    SET PickingCompletedWhen = NULL
                    WHERE OrderID = @OrderID AND StockItemID = @StockItemID;
                END
            END
            ELSE
            BEGIN
                UPDATE Warehouse.StockItemHoldings
                SET QuantityOnHand = QuantityOnHand - @RequiredQty
                WHERE StockItemID = @StockItemID;

                INSERT INTO Warehouse.StockItemTransactions (StockItemID, TransactionTypeID, Quantity)
                VALUES (@StockItemID, 1, -@RequiredQty);
            END

            FETCH NEXT FROM @LineCursor INTO @StockItemID, @RequiredQty;
        END

        CLOSE @LineCursor;
        DEALLOCATE @LineCursor;

        -- Step 4: finalize order status depending on approval/risk
        IF @ApprovalStatus = 'APPROVED'
        BEGIN
            UPDATE Sales.Orders
            SET OrderStatus = 'PROCESSED'
            WHERE OrderID = @OrderID;
        END
        ELSE IF @ApprovalStatus = 'PENDING_REVIEW'
        BEGIN
            IF @RiskLevel = 'CRITICAL'
            BEGIN
                UPDATE Sales.Orders
                SET OrderStatus = 'BLOCKED'
                WHERE OrderID = @OrderID;

                EXEC dbo.NotifyRiskTeam @OrderID = @OrderID, @RiskLevel = @RiskLevel;
            END
            ELSE
            BEGIN
                UPDATE Sales.Orders
                SET OrderStatus = 'AWAITING_APPROVAL'
                WHERE OrderID = @OrderID;
            END
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        SET @ErrorMsg = ERROR_MESSAGE();

        INSERT INTO dbo.ErrorLog (ErrorMessage, ErrorProcedure, ErrorTime)
        VALUES (@ErrorMsg, ERROR_PROCEDURE(), GETDATE());

        THROW;
    END CATCH
END
