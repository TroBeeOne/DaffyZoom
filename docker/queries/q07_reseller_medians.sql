-- Tier: ~30-60 s. Median and quartiles of reseller line amounts per territory and year, over the
-- 12M-row heap (no primary key: tracked mode must refuse this one; ttl mode may cache it).
SET NOCOUNT ON;
SELECT DISTINCT
       t.SalesTerritoryCountry,
       d.CalendarYear,
       PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY r.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Q1,
       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY r.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Median,
       PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY r.SalesAmount) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS Q3
FROM dbo.FactResellerSalesBig r
JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = r.SalesTerritoryKey
JOIN dbo.DimDate d           ON d.DateKey = r.OrderDateKey
ORDER BY t.SalesTerritoryCountry, d.CalendarYear;
