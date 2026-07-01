-- Nivel 3: vista sobre vista sobre vista (lee de vOrdersSummary).
-- Cadena completa esperada para ReportedAmount:
--   dbo.vOrdersReport.ReportedAmount
--     <- dbo.vOrdersSummary.TotalAmount
--       <- dbo.vOrdersEnriched.NetAmount
--         <- dbo.Orders.Amount   (raíz, columna de tabla base)
-- Profundidad 3 (3 saltos DERIVES_FROM hasta la raíz).
CREATE VIEW dbo.vOrdersReport AS
SELECT
    OrderID,
    TotalAmount AS ReportedAmount
FROM dbo.vOrdersSummary;
