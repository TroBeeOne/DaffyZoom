-- Inflates AdventureWorksDW2022's sales facts into tables large enough for slow queries.
-- Deterministic and idempotent: each step records itself in dbo.ZoomBuild, and a step that
-- finished is skipped on a re-run.
--
--   docker exec daffyzoom-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyZoom!Dev2026" -i /seed/inflate.sql
--
-- The stock tables are tiny (FactInternetSales is ~60k rows), so every query finishes in
-- milliseconds. This builds:
--
--   dbo.DimCustomerBig        DimCustomer x 50 (~924k customers). Key = CustomerKey + 100000 * copy.
--   dbo.FactInternetSalesBig  FactInternetSales x 500 (~30M rows). Clustered primary key, so tracked
--                             mode (Change Tracking) can use it.
--   dbo.FactResellerSalesBig  FactResellerSales x 200 (~12M rows). A heap with no primary key - the
--                             shape ODS tables often have - which tracked mode must refuse and
--                             ttl mode can still cache.
--
-- Every copy keeps the original product, date, territory and promotion keys, so joins to the stock
-- dimensions still work. Each copy gets its own order numbers and customers, and amounts scaled by
-- a few percent, so results are not just the originals times 500.
--
-- No nonclustered indexes on purpose: big reporting tables queried in ways nobody indexed for are
-- exactly what DaffyZoom is for.

SET NOCOUNT ON;
SET XACT_ABORT ON;
USE AdventureWorksDW2022;
GO

IF OBJECT_ID('dbo.ZoomBuild') IS NULL
    CREATE TABLE dbo.ZoomBuild (Step sysname NOT NULL PRIMARY KEY, FinishedUtc datetime2(0) NOT NULL, RowsBuilt bigint NOT NULL);
GO

IF OBJECT_ID('dbo.ZoomNumbers') IS NULL
BEGIN
    CREATE TABLE dbo.ZoomNumbers (n int NOT NULL PRIMARY KEY);
    INSERT dbo.ZoomNumbers (n)
    SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects a CROSS JOIN sys.all_objects b;
END
GO

-- ---- DimCustomerBig -------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.ZoomBuild WHERE Step = 'DimCustomerBig')
BEGIN
    DECLARE @started datetime2 = SYSUTCDATETIME();
    DROP TABLE IF EXISTS dbo.DimCustomerBig;

    SELECT CustomerKey = c.CustomerKey + 100000 * (n.n - 1),
           OriginalCustomerKey = c.CustomerKey,
           c.GeographyKey, c.FirstName, c.LastName, c.BirthDate, c.MaritalStatus, c.Gender,
           c.YearlyIncome, c.TotalChildren, c.NumberChildrenAtHome, c.EnglishEducation, c.EnglishOccupation,
           c.HouseOwnerFlag, c.NumberCarsOwned, c.DateFirstPurchase, c.CommuteDistance
    INTO dbo.DimCustomerBig
    FROM dbo.DimCustomer c
    CROSS JOIN dbo.ZoomNumbers n
    WHERE n.n <= 50;

    -- SELECT INTO declares the computed key nullable, and a primary key needs NOT NULL. Dynamic SQL,
    -- because SQL Server checks the whole batch when it compiles it: a DimCustomerBig left by an
    -- interrupted run would otherwise fail the check before the DROP above has run.
    -- One EXEC each: in a single one, both would again be checked before either ran.
    EXEC (N'ALTER TABLE dbo.DimCustomerBig ALTER COLUMN CustomerKey int NOT NULL;');
    EXEC (N'ALTER TABLE dbo.DimCustomerBig ADD CONSTRAINT PK_DimCustomerBig PRIMARY KEY CLUSTERED (CustomerKey);');

    INSERT dbo.ZoomBuild VALUES ('DimCustomerBig', SYSUTCDATETIME(), (SELECT COUNT_BIG(*) FROM dbo.DimCustomerBig));
    PRINT CONCAT('DimCustomerBig: built in ', DATEDIFF(second, @started, SYSUTCDATETIME()), ' s');
END
GO

-- ---- FactInternetSalesBig -------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.ZoomBuild WHERE Step = 'FactInternetSalesBig')
BEGIN
    DECLARE @started datetime2 = SYSUTCDATETIME();
    DROP TABLE IF EXISTS dbo.FactInternetSalesBig;

    CREATE TABLE dbo.FactInternetSalesBig
    (
        SalesKey             bigint IDENTITY(1,1) NOT NULL,
        CopyNo               smallint     NOT NULL,
        ProductKey           int          NOT NULL,
        OrderDateKey         int          NOT NULL,
        DueDateKey           int          NOT NULL,
        ShipDateKey          int          NOT NULL,
        CustomerKey          int          NOT NULL,
        PromotionKey         int          NOT NULL,
        CurrencyKey          int          NOT NULL,
        SalesTerritoryKey    int          NOT NULL,
        SalesOrderNumber     nvarchar(30) NOT NULL,
        SalesOrderLineNumber tinyint      NOT NULL,
        OrderQuantity        smallint     NOT NULL,
        UnitPrice            money        NOT NULL,
        ExtendedAmount       money        NOT NULL,
        UnitPriceDiscountPct float        NOT NULL,
        DiscountAmount       float        NOT NULL,
        ProductStandardCost  money        NOT NULL,
        TotalProductCost     money        NOT NULL,
        SalesAmount          money        NOT NULL,
        TaxAmt               money        NOT NULL,
        Freight              money        NOT NULL,
        OrderDate            datetime     NULL,
        DueDate              datetime     NULL,
        ShipDate             datetime     NULL,
        CONSTRAINT PK_FactInternetSalesBig PRIMARY KEY CLUSTERED (SalesKey)
    );

    -- 25 copies (~1.5M rows) per statement keeps each transaction, and the log, a sensible size.
    DECLARE @from int = 1;
    WHILE @from <= 500
    BEGIN
        INSERT dbo.FactInternetSalesBig WITH (TABLOCK)
            (CopyNo, ProductKey, OrderDateKey, DueDateKey, ShipDateKey, CustomerKey, PromotionKey, CurrencyKey,
             SalesTerritoryKey, SalesOrderNumber, SalesOrderLineNumber, OrderQuantity, UnitPrice, ExtendedAmount,
             UnitPriceDiscountPct, DiscountAmount, ProductStandardCost, TotalProductCost, SalesAmount, TaxAmt,
             Freight, OrderDate, DueDate, ShipDate)
        SELECT n.n, f.ProductKey, f.OrderDateKey, f.DueDateKey, f.ShipDateKey,
               f.CustomerKey + 100000 * ((n.n - 1) % 50),
               f.PromotionKey, f.CurrencyKey, f.SalesTerritoryKey,
               CONCAT(f.SalesOrderNumber, N'-', RIGHT(CONCAT(N'000', n.n), 3)), f.SalesOrderLineNumber,
               f.OrderQuantity, f.UnitPrice,
               f.ExtendedAmount * s.Scale, f.UnitPriceDiscountPct, f.DiscountAmount,
               f.ProductStandardCost, f.TotalProductCost * s.Scale,
               f.SalesAmount * s.Scale, f.TaxAmt * s.Scale, f.Freight * s.Scale,
               f.OrderDate, f.DueDate, f.ShipDate
        FROM dbo.FactInternetSales f
        CROSS JOIN dbo.ZoomNumbers n
        CROSS APPLY (SELECT Scale = CAST(0.90 + (n.n % 21) / 100.0 AS money)) s
        WHERE n.n BETWEEN @from AND @from + 24
        ORDER BY n.n, f.SalesOrderNumber, f.SalesOrderLineNumber;

        CHECKPOINT;
        SET @from += 25;
    END

    INSERT dbo.ZoomBuild VALUES ('FactInternetSalesBig', SYSUTCDATETIME(), (SELECT COUNT_BIG(*) FROM dbo.FactInternetSalesBig));
    PRINT CONCAT('FactInternetSalesBig: built in ', DATEDIFF(second, @started, SYSUTCDATETIME()), ' s');
END
GO

-- ---- FactResellerSalesBig (heap, no primary key) ---------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.ZoomBuild WHERE Step = 'FactResellerSalesBig')
BEGIN
    DECLARE @started datetime2 = SYSUTCDATETIME();
    DROP TABLE IF EXISTS dbo.FactResellerSalesBig;

    CREATE TABLE dbo.FactResellerSalesBig
    (
        CopyNo               smallint     NOT NULL,
        ProductKey           int          NOT NULL,
        OrderDateKey         int          NOT NULL,
        ResellerKey          int          NOT NULL,
        EmployeeKey          int          NOT NULL,
        PromotionKey         int          NOT NULL,
        SalesTerritoryKey    int          NOT NULL,
        SalesOrderNumber     nvarchar(30) NOT NULL,
        SalesOrderLineNumber tinyint      NOT NULL,
        OrderQuantity        smallint     NOT NULL,
        UnitPrice            money        NOT NULL,
        ExtendedAmount       money        NOT NULL,
        DiscountAmount       float        NOT NULL,
        TotalProductCost     money        NOT NULL,
        SalesAmount          money        NOT NULL,
        TaxAmt               money        NOT NULL,
        Freight              money        NOT NULL,
        OrderDate            datetime     NULL
    );

    DECLARE @from int = 1;
    WHILE @from <= 200
    BEGIN
        INSERT dbo.FactResellerSalesBig WITH (TABLOCK)
        SELECT n.n, f.ProductKey, f.OrderDateKey, f.ResellerKey, f.EmployeeKey, f.PromotionKey, f.SalesTerritoryKey,
               CONCAT(f.SalesOrderNumber, N'-', RIGHT(CONCAT(N'000', n.n), 3)), f.SalesOrderLineNumber,
               f.OrderQuantity, f.UnitPrice, f.ExtendedAmount * s.Scale, f.DiscountAmount,
               f.TotalProductCost * s.Scale, f.SalesAmount * s.Scale, f.TaxAmt * s.Scale, f.Freight * s.Scale,
               f.OrderDate
        FROM dbo.FactResellerSales f
        CROSS JOIN dbo.ZoomNumbers n
        CROSS APPLY (SELECT Scale = CAST(0.90 + (n.n % 21) / 100.0 AS money)) s
        WHERE n.n BETWEEN @from AND @from + 24;

        CHECKPOINT;
        SET @from += 25;
    END

    INSERT dbo.ZoomBuild VALUES ('FactResellerSalesBig', SYSUTCDATETIME(), (SELECT COUNT_BIG(*) FROM dbo.FactResellerSalesBig));
    PRINT CONCAT('FactResellerSalesBig: built in ', DATEDIFF(second, @started, SYSUTCDATETIME()), ' s');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.ZoomBuild WHERE Step = 'Statistics')
BEGIN
    UPDATE STATISTICS dbo.DimCustomerBig;
    UPDATE STATISTICS dbo.FactInternetSalesBig;
    UPDATE STATISTICS dbo.FactResellerSalesBig;
    INSERT dbo.ZoomBuild VALUES ('Statistics', SYSUTCDATETIME(), 0);
END
GO

SELECT Step, FinishedUtc, RowsBuilt FROM dbo.ZoomBuild ORDER BY FinishedUtc;
PRINT 'inflate.sql: done';
