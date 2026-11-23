-- Tier: ~15 s. Customer lifetime value: distinct orders per customer over 30M rows, then the top
-- thousand with their demographics.
SET NOCOUNT ON;
WITH per_customer AS
(
    SELECT f.CustomerKey,
           COUNT(DISTINCT f.SalesOrderNumber) AS Orders,
           SUM(f.SalesAmount)                 AS Lifetime,
           MIN(f.OrderDate)                   AS FirstOrder,
           MAX(f.OrderDate)                   AS LastOrder
    FROM dbo.FactInternetSalesBig f
    GROUP BY f.CustomerKey
)
SELECT TOP (1000)
       c.CustomerKey, c.FirstName, c.LastName, c.YearlyIncome, c.EnglishOccupation, g.EnglishCountryRegionName AS Country,
       pc.Orders, pc.Lifetime, pc.FirstOrder, pc.LastOrder
FROM per_customer pc
JOIN dbo.DimCustomerBig c ON c.CustomerKey = pc.CustomerKey
JOIN dbo.DimGeography g   ON g.GeographyKey = c.GeographyKey
ORDER BY pc.Lifetime DESC, c.CustomerKey;
