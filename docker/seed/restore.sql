-- Restores AdventureWorksDW2022 from /backups and creates the test logins. Idempotent.
--
--   docker exec daffyzoom-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyZoom!Dev2026" -i /seed/restore.sql
--
-- Logins, standing in for the shapes a DaffyZoom mapping takes:
--   zoom_reports   a shared reporting account: reads everything. ttl mode needs nothing more.
--   zoom_tracked   the same, plus what tracked mode needs (Change Tracking, metadata). Grants are
--                  added by fixtures.sql once the objects exist.
--   zoom_limited   reads the dimensions but not the fact tables: permission tests.

SET NOCOUNT ON;

IF DB_ID('AdventureWorksDW2022') IS NULL
BEGIN
    -- The backup's logical file names, read rather than assumed.
    DECLARE @files TABLE
    (
        LogicalName nvarchar(128), PhysicalName nvarchar(260), [Type] char(1), FileGroupName nvarchar(128) NULL,
        Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0) NULL,
        UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0) NULL, ReadWriteLSN numeric(25,0) NULL,
        BackupSizeInBytes bigint, SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier NULL,
        DifferentialBaseLSN numeric(25,0) NULL, DifferentialBaseGUID uniqueidentifier NULL, IsReadOnly bit,
        IsPresent bit, TDEThumbprint varbinary(32) NULL, SnapshotUrl nvarchar(360) NULL
    );
    INSERT @files EXEC ('RESTORE FILELISTONLY FROM DISK = N''/backups/AdventureWorksDW2022.bak''');

    DECLARE @data nvarchar(128) = (SELECT TOP 1 LogicalName FROM @files WHERE [Type] = 'D');
    DECLARE @log  nvarchar(128) = (SELECT TOP 1 LogicalName FROM @files WHERE [Type] = 'L');
    DECLARE @sql nvarchar(max) =
        N'RESTORE DATABASE AdventureWorksDW2022 FROM DISK = N''/backups/AdventureWorksDW2022.bak'' WITH ' +
        N'MOVE ' + QUOTENAME(@data, '''') + N' TO N''/var/opt/mssql/data/AdventureWorksDW2022.mdf'', ' +
        N'MOVE ' + QUOTENAME(@log, '''') + N' TO N''/var/opt/mssql/data/AdventureWorksDW2022_log.ldf'', STATS = 25';
    EXEC (@sql);
END
GO

-- The inflate writes tens of millions of rows; SIMPLE keeps the log from growing with them.
ALTER DATABASE AdventureWorksDW2022 SET RECOVERY SIMPLE;
ALTER DATABASE AdventureWorksDW2022 SET COMPATIBILITY_LEVEL = 160;
GO

IF SUSER_ID('zoom_reports') IS NULL
    CREATE LOGIN zoom_reports WITH PASSWORD = 'Zoom!Reports2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = AdventureWorksDW2022;
IF SUSER_ID('zoom_tracked') IS NULL
    CREATE LOGIN zoom_tracked WITH PASSWORD = 'Zoom!Tracked2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = AdventureWorksDW2022;
IF SUSER_ID('zoom_limited') IS NULL
    CREATE LOGIN zoom_limited WITH PASSWORD = 'Zoom!Limited2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = AdventureWorksDW2022;
GO

USE AdventureWorksDW2022;
GO

IF USER_ID('zoom_reports') IS NULL CREATE USER zoom_reports FOR LOGIN zoom_reports;
IF USER_ID('zoom_tracked') IS NULL CREATE USER zoom_tracked FOR LOGIN zoom_tracked;
IF USER_ID('zoom_limited') IS NULL CREATE USER zoom_limited FOR LOGIN zoom_limited;
ALTER ROLE db_datareader ADD MEMBER zoom_reports;
ALTER ROLE db_datareader ADD MEMBER zoom_tracked;
GRANT EXECUTE TO zoom_reports;
GRANT EXECUTE TO zoom_tracked;
-- So SSMS's execution plans work through DaffyZoom as zoom_reports: a session that asks for one
-- (SET STATISTICS XML / SHOWPLAN_XML) is never served from the cache, and this shows it.
GRANT SHOWPLAN TO zoom_reports;
GO

PRINT 'restore.sql: done';
