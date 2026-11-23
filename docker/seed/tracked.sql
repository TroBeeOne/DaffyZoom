-- What tracked mode needs on the test database: Change Tracking on the database and on every
-- table the test queries read that has a primary key, and zoom_tracked's grants. Idempotent.
--
--   docker exec daffyzoom-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyZoom!Dev2026" -i /seed/tracked.sql
--
-- dbo.FactResellerSalesBig is a heap: Change Tracking needs a primary key, so it stays untracked,
-- and a query that reads it must be refused in tracked mode (NoChangeTracking).
--
-- The grants are what README.md lists for tracked mode, and no more: zoom_tracked already reads
-- everything (db_datareader, from restore.sql); here it gets VIEW DEFINITION, to resolve views and
-- functions to their tables, and VIEW CHANGE TRACKING, to ask whether those tables changed.

SET NOCOUNT ON;
USE AdventureWorksDW2022;
GO

IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
    ALTER DATABASE AdventureWorksDW2022 SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
GO

DECLARE @tables TABLE (name sysname PRIMARY KEY);
INSERT @tables VALUES
    (N'dbo.FactInternetSalesBig'), (N'dbo.DimCustomerBig'), (N'dbo.DimDate'), (N'dbo.DimProduct'),
    (N'dbo.DimProductSubcategory'), (N'dbo.DimProductCategory'), (N'dbo.DimSalesTerritory'),
    (N'dbo.DimGeography'), (N'etl.LoadStatus');

DECLARE @name sysname, @sql nvarchar(400);
DECLARE tables CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name FROM @tables t
    WHERE NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables c WHERE c.object_id = OBJECT_ID(t.name));
OPEN tables;
FETCH NEXT FROM tables INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER TABLE ' + @name + N' ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);';
    EXEC (@sql);
    PRINT 'change tracking on ' + @name;
    FETCH NEXT FROM tables INTO @name;
END
CLOSE tables;
DEALLOCATE tables;
GO

GRANT VIEW DEFINITION TO zoom_tracked;
GRANT VIEW CHANGE TRACKING ON SCHEMA::dbo TO zoom_tracked;
GRANT VIEW CHANGE TRACKING ON SCHEMA::etl TO zoom_tracked;
GO

SELECT OBJECT_SCHEMA_NAME(object_id) + '.' + OBJECT_NAME(object_id) AS tracked_table
FROM sys.change_tracking_tables ORDER BY 1;
PRINT 'tracked.sql: done';
GO
