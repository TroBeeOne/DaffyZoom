-- Tier: ~10 s, and large: the 500,000 biggest lines, sorted out of 30M - slow because of the sort,
-- and big enough (tens of MB) to test streaming a cached result and the entry size cap.
SET NOCOUNT ON;
SELECT TOP (500000)
       f.SalesKey, f.SalesOrderNumber, f.SalesOrderLineNumber, f.OrderDate, f.CustomerKey, f.ProductKey,
       f.OrderQuantity, f.UnitPrice, f.SalesAmount, f.TaxAmt, f.Freight
FROM dbo.FactInternetSalesBig f
ORDER BY f.SalesAmount DESC, f.SalesKey;
