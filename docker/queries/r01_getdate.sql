-- Must be refused (NonDeterministic): the answer depends on when it runs, not only on the data.
SET NOCOUNT ON;
SELECT d.CalendarYear, SUM(f.SalesAmount) AS Sales
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey
WHERE f.OrderDate < DATEADD(year, -10, GETDATE())
GROUP BY d.CalendarYear
ORDER BY d.CalendarYear;
