-- Caso: MERGE con OUTPUT INTO una tabla de log (pseudo-tablas inserted/deleted).
-- ARREGLADO: el lineage por OUTPUT ahora se extrae. La causa era doble: (1) ScriptDOM
-- expone OUTPUT...INTO en `.OutputIntoClause`, no en `.OutputClause`; (2) GraphExporter no
-- contemplaba el tipo de consecuencia "OUTPUT" como escritura de tabla.
-- Esperado: ProductMergeLog.NewPrice <- TargetProducts.Price (vía inserted) y
-- OldPrice <- TargetProducts.Price (vía deleted), además del lineage de UPDATE/INSERT.
CREATE PROCEDURE dbo.usp_SyncProducts AS
BEGIN
  MERGE dbo.TargetProducts AS t
  USING dbo.SourceProducts AS s ON t.Id = s.Id
  WHEN MATCHED THEN UPDATE SET t.Price = s.Price
  WHEN NOT MATCHED THEN INSERT (Id, Price) VALUES (s.Id, s.Price)
  OUTPUT deleted.Price AS OldPrice, inserted.Price AS NewPrice
  INTO dbo.ProductMergeLog (OldPrice, NewPrice);
END
