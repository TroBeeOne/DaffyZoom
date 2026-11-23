-- Objects the DaffyZoom tests query, on top of the inflated tables. Idempotent.
--
--   docker exec daffyzoom-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyZoom!Dev2026" -i /seed/fixtures.sql
--
-- Each one stands in for something DaffyZoom has to get right:
--   etl.LoadStatus          an ETL control table: ttl mode's watermark poll watches its last row
--   dbo.vZoomSales          a view over the big fact table and five dimensions
--   dbo.vZoomSalesMonthly   a view on that view - dependencies two levels deep
--   dbo.fnZoomMarginPct     a scalar function used inside a query
--   dbo.usp_ZoomTerritoryYear  a stored procedure - cached only when opted in by name

SET NOCOUNT ON;
USE AdventureWorksDW2022;
GO

IF SCHEMA_ID('etl') IS NULL EXEC ('CREATE SCHEMA etl');
GO

IF OBJECT_ID('etl.LoadStatus') IS NULL
BEGIN
    CREATE TABLE etl.LoadStatus (LoadId int IDENTITY(1,1) PRIMARY KEY, CompletedUtc datetime2(3) NOT NULL);
    INSERT etl.LoadStatus (CompletedUtc) VALUES (SYSUTCDATETIME());
END
GO

CREATE OR ALTER VIEW dbo.vZoomSales
AS
SELECT f.SalesKey, f.CopyNo, f.OrderDateKey, d.CalendarYear, d.MonthNumberOfYear, f.CustomerKey,
       f.SalesTerritoryKey, t.SalesTerritoryGroup, t.SalesTerritoryCountry, t.SalesTerritoryRegion,
       f.ProductKey, p.EnglishProductName AS ProductName,
       sc.EnglishProductSubcategoryName AS Subcategory, pc.EnglishProductCategoryName AS Category,
       f.SalesOrderNumber, f.OrderQuantity, f.SalesAmount, f.TotalProductCost, f.TaxAmt, f.Freight
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey
JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = f.SalesTerritoryKey
JOIN dbo.DimProduct p ON p.ProductKey = f.ProductKey
LEFT JOIN dbo.DimProductSubcategory sc ON sc.ProductSubcategoryKey = p.ProductSubcategoryKey
LEFT JOIN dbo.DimProductCategory pc ON pc.ProductCategoryKey = sc.ProductCategoryKey;
GO

CREATE OR ALTER VIEW dbo.vZoomSalesMonthly
AS
SELECT CalendarYear, MonthNumberOfYear, Category, SalesTerritoryGroup,
       SUM(SalesAmount) AS Sales, SUM(TotalProductCost) AS Cost, COUNT_BIG(*) AS Lines
FROM dbo.vZoomSales
GROUP BY CalendarYear, MonthNumberOfYear, Category, SalesTerritoryGroup;
GO

CREATE OR ALTER FUNCTION dbo.fnZoomMarginPct (@sales money, @cost money)
RETURNS decimal(9,4)
AS
BEGIN
    RETURN CASE WHEN @sales = 0 THEN NULL ELSE CAST((@sales - @cost) / @sales * 100 AS decimal(9,4)) END;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_ZoomTerritoryYear @Year int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SalesTerritoryGroup, SalesTerritoryCountry, Category,
           SUM(SalesAmount) AS Sales, COUNT(DISTINCT SalesOrderNumber) AS Orders
    FROM dbo.vZoomSales
    WHERE CalendarYear = @Year
    GROUP BY SalesTerritoryGroup, SalesTerritoryCountry, Category
    ORDER BY SalesTerritoryGroup, SalesTerritoryCountry, Category;
END
GO

-- The ETL control table is written as a load finishes; r04_write.sql does that through DaffyZoom
-- as zoom_reports, to check that a write it sees flushes the database.
GRANT INSERT ON etl.LoadStatus TO zoom_reports;
GO

-- zoom_limited may read the dimensions, not the facts: a query it sends must fail at SQL Server,
-- never be answered from an entry another login filled.
GRANT SELECT ON dbo.DimDate TO zoom_limited;
GRANT SELECT ON dbo.DimProduct TO zoom_limited;
GRANT SELECT ON dbo.DimProductSubcategory TO zoom_limited;
GRANT SELECT ON dbo.DimProductCategory TO zoom_limited;
GRANT SELECT ON dbo.DimSalesTerritory TO zoom_limited;
GO

PRINT 'fixtures.sql: done';
