# Test Case: Dynamic Trigger Creation

This case tests the parser's ability to handle DDL (`CREATE TRIGGER`) inside dynamic SQL (`EXEC sp_executesql`).

## `setup.sql`

-   Defines two tables: `dbo.Customers` and `dbo.Customers_Audit`.
-   Defines a stored procedure `dbo.usp_SetupTriggers` that dynamically builds and executes a `CREATE TRIGGER` statement.
-   The trigger, `TR_Audit_Customers`, fires on `UPDATE` of `dbo.Customers` and inserts the old data into `dbo.Customers_Audit`.

## Expected Behavior (`expected-lineage.json`)

The toolkit should:
1.  Resolve the dynamic SQL inside `usp_SetupTriggers`.
2.  Identify the `CREATE TRIGGER` statement.
3.  Create a new `:SqlObject` node for the trigger `dbo.TR_Audit_Customers`.
4.  Create a `CREATES` edge from the procedure to the trigger.
5.  Create an `ON` edge from the trigger to the table `dbo.Customers`.
6.  Parse the body of the trigger to extract its DML lineage.
7.  Create a `WRITES_TO` edge from the trigger to the audit table `dbo.Customers_Audit`.
8.  Trace column-level lineage from `Customers_Audit` back to `Customers`, correctly resolving `inserted.*` and `deleted.*` to the base table columns.