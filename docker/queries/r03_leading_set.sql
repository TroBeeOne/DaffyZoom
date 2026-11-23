-- Cacheable, with a leading SET: on a hit DaffyZoom must send the SET to the server itself, so the
-- session ends up as if the batch had run there.
SET NOCOUNT ON;
SET ARITHABORT ON;
SELECT sc.EnglishProductSubcategoryName AS Subcategory, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales
FROM dbo.FactInternetSalesBig f
JOIN dbo.DimProduct p             ON p.ProductKey = f.ProductKey
JOIN dbo.DimProductSubcategory sc ON sc.ProductSubcategoryKey = p.ProductSubcategoryKey
GROUP BY sc.EnglishProductSubcategoryName
ORDER BY Sales DESC;
