-- Tier: ~5 min. A distribution profile of internet sales per country and year: medians and 90th
-- percentiles of six measures over all 30M rows, each its own sort, on one core. The longest of the
-- set: the "minutes become a second" case.
SET NOCOUNT ON;
SELECT DISTINCT
       t.SalesTerritoryCountry,
       d.CalendarYear,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.SalesAmount)      OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS MedianSales,
       PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY f.SalesAmount)      OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS P90Sales,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.TaxAmt)           OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS MedianTax,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.Freight)          OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS MedianFreight,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.UnitPrice)        OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS MedianPrice,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY f.TotalProductCost) OVER (PARTITION BY t.SalesTerritoryCountry, d.CalendarYear) AS MedianCost
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = f.SalesTerritoryKey
JOIN dbo.DimDate d           ON d.DateKey = f.OrderDateKey
ORDER BY t.SalesTerritoryCountry, d.CalendarYear
OPTION (MAXDOP 1);
