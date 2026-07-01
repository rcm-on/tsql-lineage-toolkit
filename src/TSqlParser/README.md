# MERGE with OUTPUT Lineage Test Case

This case tests the parser's ability to trace column lineage through a complex `MERGE` statement that includes an `OUTPUT` clause.

- **`source.sql`**: Creates source, target, and log tables, and a procedure `usp_SyncProducts` that contains the `MERGE` logic.

## The Challenge

A `MERGE` statement represents a multi-faceted data flow that is a significant test for lineage extraction:

1.  **Multiple Reads**: It reads from both the `TARGET` and `USING` (source) tables.
2.  **Conditional Writes**: It performs `UPDATE`, `INSERT`, and `DELETE` operations on the `TARGET` table.
3.  **Secondary Write**: The `OUTPUT` clause performs an `INSERT` into a separate log table.
4.  **Implicit Lineage**: The lineage of columns written to the log table flows through the `inserted` and `deleted` pseudo-tables.

### Expected Lineage Connections

- **Table Reads**: `usp_SyncProducts` should read from `SourceProducts` and `TargetProducts`.
- **Table Writes**: `usp_SyncProducts` should write to `TargetProducts` and `ProductMergeLog`.
- **Column Flow (Update)**: `TargetProducts.Price` should derive from `SourceProducts.Price`.
- **Column Flow (Insert)**: `TargetProducts.Price` should derive from `SourceProducts.Price`.
- **Column Flow (Output)**: `ProductMergeLog.NewPrice` should derive from `TargetProducts.Price` (via `inserted`), which in turn derives from `SourceProducts.Price`.

This case will reveal if the parser can correctly identify all read/write dependencies and trace the column-level data flow through the entire operation.