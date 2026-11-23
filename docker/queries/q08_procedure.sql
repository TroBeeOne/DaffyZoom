-- Tier: ~10 s. A stored procedure: cached only when the database's policy allows it by name.
SET NOCOUNT ON;
EXEC dbo.usp_ZoomTerritoryYear @Year = 2013;
