-- Tier: ~70 s. Median and quartiles of every internet sales line per country and year: three
-- PERCENTILE_CONT windows over all 30M rows of the big fact, on one core.
SET NOCOUNT ON;
SELECT DISTINCT
       t.SalesTerritoryCountry,
       d.CalendarYear,
       PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY f.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Q1,
       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY f.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Median,
       PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY f.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Q3
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = f.SalesTerritoryKey
JOIN dbo.DimDate d           ON d.DateKey = f.OrderDateKey
ORDER BY t.SalesTerritoryCountry, d.CalendarYear
OPTION (MAXDOP 1);
