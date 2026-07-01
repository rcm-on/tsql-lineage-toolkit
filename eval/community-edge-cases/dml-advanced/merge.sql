-- Caso: MERGE con WHEN MATCHED UPDATE SET + WHEN NOT MATCHED INSERT VALUES.
-- Gap original: 0 lineage de columna (UPDATE/INSERT tratados como caja negra).
-- Tras el fix (AstWalker.MergeLineage): cada columna destino DERIVES_FROM su columna
-- fuente. Esperado: DERIVES_FROM TargetProducts.Price <- SourceProducts.Price (x2: UPDATE
-- e INSERT) y TargetProducts.Id <- SourceProducts.Id; WRITES_COLUMN Price/Id.
CREATE PROCEDURE dbo.usp_Sync AS
BEGIN
  MERGE dbo.TargetProducts AS t
  USING dbo.SourceProducts AS s ON t.Id = s.Id
  WHEN MATCHED THEN UPDATE SET t.Price = s.Price
  WHEN NOT MATCHED THEN INSERT (Id, Price) VALUES (s.Id, s.Price);
END
