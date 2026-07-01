-- CONTROL (tabla limpia): PK presente, solo lectura. Fuente del dato "contaminado"
-- que el procedimiento de inyección concatena en SQL dinámico.
-- Esperado: NINGÚN hallazgo.
CREATE TABLE dbo.SearchConfig
(
    ConfigKey   VARCHAR(50)   NOT NULL PRIMARY KEY,
    DefaultSort NVARCHAR(100) NOT NULL
);
