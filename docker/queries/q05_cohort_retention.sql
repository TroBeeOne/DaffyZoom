-- Tier: ~60 s (MAXDOP 1). Cohort retention: each customer's first month, then how many of each cohort bought
-- again N months later - a distinct count over a join of the fact with itself, aggregated.
SET NOCOUNT ON;
WITH first_purchase AS
(
    SELECT CustomerKey, DATEFROMPARTS(YEAR(MIN(OrderDate)), MONTH(MIN(OrderDate)), 1) AS Cohort
    FROM dbo.FactInternetSalesBig
    GROUP BY CustomerKey
),
activity AS
(
    SELECT fp.Cohort,
           DATEDIFF(month, fp.Cohort, f.OrderDate) AS MonthOffset,
           f.CustomerKey
    FROM dbo.FactInternetSalesBig f
    JOIN first_purchase fp ON fp.CustomerKey = f.CustomerKey
)
SELECT Cohort, MonthOffset, COUNT(DISTINCT CustomerKey) AS ActiveCustomers
FROM activity
GROUP BY Cohort, MonthOffset
ORDER BY Cohort, MonthOffset
OPTION (MAXDOP 1);
