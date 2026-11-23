-- Tier: >= 5 s. Sales and distinct customers by year and product category: one scan of the
-- 30M-row fact, four joins, a distinct count. MAXDOP 1, because with four cores and the data in
-- memory SQL Server finishes the parallel plan in about a second.
SET NOCOUNT ON;
SELECT d.CalendarYear,
       pc.EnglishProductCategoryName AS Category,
       SUM(f.SalesAmount)            AS Sales,
       SUM(f.OrderQuantity)          AS Units,
       COUNT(DISTINCT f.CustomerKey) AS Customers,
       COUNT_BIG(*)                  AS Lines
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimDate d                ON d.DateKey = f.OrderDateKey
JOIN dbo.DimProduct p             ON p.ProductKey = f.ProductKey
JOIN dbo.DimProductSubcategory sc ON sc.ProductSubcategoryKey = p.ProductSubcategoryKey
JOIN dbo.DimProductCategory pc    ON pc.ProductCategoryKey = sc.ProductCategoryKey
GROUP BY d.CalendarYear, pc.EnglishProductCategoryName
ORDER BY d.CalendarYear, Category
OPTION (MAXDOP 1);
