-- Tier: ~45 s (MAXDOP 1). Market basket: which products are bought together - the fact joined to itself on
-- order number, every pair counted.
SET NOCOUNT ON;
SELECT TOP (50)
       pa.EnglishProductName AS ProductA,
       pb.EnglishProductName AS ProductB,
       x.Together
FROM
(
    SELECT a.ProductKey AS ProductA, b.ProductKey AS ProductB, COUNT_BIG(*) AS Together
    FROM dbo.FactInternetSalesBig a
    JOIN dbo.FactInternetSalesBig b
      ON b.SalesOrderNumber = a.SalesOrderNumber
     AND b.ProductKey > a.ProductKey
    GROUP BY a.ProductKey, b.ProductKey
) x
JOIN dbo.DimProduct pa ON pa.ProductKey = x.ProductA
JOIN dbo.DimProduct pb ON pb.ProductKey = x.ProductB
ORDER BY x.Together DESC, ProductA, ProductB
OPTION (MAXDOP 1);
