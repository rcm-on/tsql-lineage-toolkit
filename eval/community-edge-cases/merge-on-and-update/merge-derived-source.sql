-- Caso: MERGE cuyo USING es una tabla derivada de solo variables/parametros (sin tabla
-- base real), como dbo.UpdateHostSetting del corpus DNN (ver eval/column-recall/blind-refs.md,
-- causa #2, ejemplo real). El WHEN MATCHED THEN UPDATE SET asigna la columna destino desde
-- esa tabla derivada: el motor no puede trazar DERIVES_FROM (no hay tabla fuente real que
-- resolver), pero la columna destino SIGUE siendo escrita y su nombre es estatico y conocido -
-- debe llevar WRITES_COLUMN igual que una UPDATE normal, no desaparecer del grafo.
CREATE PROCEDURE dbo.UpdateHostSetting
  @SettingName nvarchar(50),
  @SettingValue nvarchar(max),
  @UserID int
AS
BEGIN
  MERGE INTO dbo.HostSettings AS S
  USING (SELECT @SettingName AS SN, @SettingValue AS SV) AS Q
    ON (S.SettingName = Q.SN)
  WHEN MATCHED THEN
    UPDATE SET S.SettingValue = Q.SV, S.LastModifiedByUserID = @UserID, S.LastModifiedOnDate = GetDate()
  WHEN NOT MATCHED THEN
    INSERT (SettingName, SettingValue, LastModifiedByUserID, LastModifiedOnDate)
    VALUES (Q.SN, Q.SV, @UserID, GetDate());
END
