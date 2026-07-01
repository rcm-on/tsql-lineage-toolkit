-- SINK DE AUDITORÍA mal diseñado: SIN clave primaria y solo recibe escrituras
-- (ningún objeto analizado la lee).
-- Esperado (tabla):
--   "Tabla sin clave primaria"        (med, Integridad)
--   "Tabla escrita pero nunca leída"  (low, Diseño)
CREATE TABLE dbo.OrderAudit
(
    OrderId  INT           NULL,
    Action   NVARCHAR(400) NULL,
    LoggedAt DATETIME2      NULL
);
