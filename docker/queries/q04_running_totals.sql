-- Tier: ~30 s (MAXDOP 2). Running totals per customer across all 30M lines (a full sort), then when each
-- country's customers first passed 5,000 in lifetime spend.
SET NOCOUNT ON;
WITH running AS
(
    SELECT f.CustomerKey, f.OrderDate, f.SalesTerritoryKey,
           SUM(f.SalesAmount) OVER (PARTITION BY f.CustomerKey ORDER BY f.OrderDate, f.SalesKey
                                    ROWS UNBOUNDED PRECEDING) AS RunningTotal
    FROM dbo.FactInternetSalesBig f
),
crossed AS
(
    SELECT CustomerKey, SalesTerritoryKey, MIN(OrderDate) AS CrossedOn
    FROM running
    WHERE RunningTotal >= 5000
    GROUP BY CustomerKey, SalesTerritoryKey
)
SELECT t.SalesTerritoryCountry, YEAR(c.CrossedOn) AS YearCrossed, COUNT_BIG(*) AS Customers
FROM crossed c
JOIN dbo.DimSalesTerritory t ON t.SalesTerritoryKey = c.SalesTerritoryKey
GROUP BY t.SalesTerritoryCountry, YEAR(c.CrossedOn)
ORDER BY t.SalesTerritoryCountry, YearCrossed
OPTION (MAXDOP 2);
