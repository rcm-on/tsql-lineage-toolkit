-- Caso: MERGE con ON sobre columnas de las DOS tablas (target y source reales) y
-- WHEN MATCHED THEN UPDATE SET tomando valor de la fuente.
-- Gap (T8, causa #2 de eval/column-recall/blind-refs.md): la condicion ON no genera
-- ninguna arista de columna (ni FILTERS_ON), solo READS_FROM/DERIVES_FROM cubren
-- parcialmente el resto. Aqui S.Code (target) y Q.Code (source) participan en el ON
-- y deberian aparecer como "columnas que decidieron que filas se tocaron".
CREATE PROCEDURE dbo.usp_MergeOnBothSides AS
BEGIN
  MERGE dbo.TargetSettings AS S
  USING dbo.SourceSettings AS Q ON S.Code = Q.Code AND S.Region = Q.Region
  WHEN MATCHED THEN UPDATE SET S.Value = Q.Value
  WHEN NOT MATCHED THEN INSERT (Code, Region, Value) VALUES (Q.Code, Q.Region, Q.Value);
END
