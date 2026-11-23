-- Tier: >= 5 s. Through a view on a view (vZoomSalesMonthly over vZoomSales): dependencies two
-- deep, and a scalar function. MAXDOP 1 keeps it above the slow threshold.
SET NOCOUNT ON;
SELECT CalendarYear, MonthNumberOfYear, Category, SalesTerritoryGroup, Sales, Cost,
       dbo.fnZoomMarginPct(Sales, Cost) AS MarginPct, Lines
FROM dbo.vZoomSalesMonthly
ORDER BY CalendarYear, MonthNumberOfYear, Category, SalesTerritoryGroup
OPTION (MAXDOP 1);
