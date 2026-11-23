-- Must be refused (TempTable): a temporary table is the session's own, and creating one changes
-- the server session in a way a cached answer cannot.
SET NOCOUNT ON;
SELECT ProductKey, SUM(SalesAmount) AS Sales
INTO #by_product
FROM dbo.FactInternetSalesBig
GROUP BY ProductKey;

SELECT p.EnglishProductName, b.Sales
FROM #by_product b
JOIN dbo.DimProduct p ON p.ProductKey = b.ProductKey
ORDER BY b.Sales DESC;

DROP TABLE #by_product;
