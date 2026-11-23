-- Must be refused (Write). Seen through DaffyZoom against a ttl-mode database, it also flushes that
-- database's cache. It writes only the ETL control table, so the big tables stay as built.
SET NOCOUNT ON;
INSERT etl.LoadStatus (CompletedUtc) VALUES (SYSUTCDATETIME());
SELECT TOP (1) LoadId, CompletedUtc FROM etl.LoadStatus ORDER BY LoadId DESC;
