-- REGLA OBJETIVO: "Tabla ancha" (low, Diseño).
-- 14 columnas que mezclan dimensiones (producto, proveedor, logística, marketing):
-- tabla "Dios" candidata a normalizar. Tiene PK y NOT NULL para aislar el hallazgo
-- (no debe disparar "sin PK" ni "totalmente anulable").
CREATE TABLE dbo.WideProductCatalog
(
    ProductId       INT           NOT NULL PRIMARY KEY,
    ProductName     NVARCHAR(200) NOT NULL,
    SupplierName    NVARCHAR(200) NOT NULL,
    SupplierEmail   NVARCHAR(200) NOT NULL,
    CategoryName    NVARCHAR(100) NOT NULL,
    UnitPrice       DECIMAL(18,2) NOT NULL,
    UnitsInStock    INT           NOT NULL,
    WarehouseAisle  VARCHAR(20)   NOT NULL,
    WarehouseBin    VARCHAR(20)   NOT NULL,
    WeightKg        DECIMAL(10,3) NOT NULL,
    MarketingTag    NVARCHAR(100) NOT NULL,
    SeoSlug         NVARCHAR(200) NOT NULL,
    CreatedBy       NVARCHAR(100) NOT NULL,
    CreatedAt       DATETIME2     NOT NULL
);
