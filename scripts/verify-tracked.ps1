<#
.SYNOPSIS
    End-to-end check of tracked mode: a real DaffyZoom, a real client, the real test SQL Server with
    Change Tracking on (docker/seed/tracked.sql), and every hit checked against it.

.DESCRIPTION
    Builds the solution, maps logins in a throwaway config directory, puts the test database in
    tracked mode, starts DaffyZoom on its own port, and checks:

      - a slow query's third run is served after Change Tracking says nothing it reads changed
      - a view on a view with a scalar function resolves to its tables and is served
      - a parameterised query (sp_executesql) the same way
      - a change to a table the query reads sends it to SQL Server, which fills it again
      - a change to a table it does not read leaves it served (what ttl mode cannot tell apart)
      - a query on a table without Change Tracking (a heap) is refused, saying why
      - a dirty read (NOLOCK) is never cached in tracked mode
      - a login without VIEW DEFINITION / VIEW CHANGE TRACKING is never served, and told why
      - access denied after an answer was cached ends in SQL Server's own permission error
      - daffyzoom.tracking shows each login's view of Change Tracking: the tracked tables and the
        entries on them, with 'all' the heap and why it cannot be tracked, and for a login without
        the grants, the GRANTs it lacks
      - DaffyZoom logged no errors

    It needs the test server from scripts/setup-testdb.ps1, including tracked.sql. It changes data
    only by updating a row to its own values (which Change Tracking still records), and puts back
    the one permission it denies.
#>
param(
    [int]$Port = 14405,
    [string]$Server = "127.0.0.1,14340",
    [string]$Database = "AdventureWorksDW2022",
    [string]$SaPassword = "DaffyZoom!Dev2026",
    [string]$Configuration = "Debug",
    [int]$SlowMs = 1000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$queries = Join-Path $root 'docker/queries'
$script:failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function Wait-Port([int]$port, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 200 }
    }
    return $false
}

function Via([string]$login, [string]$password, [string]$extra = "") {
    "Server=tcp:127.0.0.1,$Port;User Id=$login;Password=$password;Encrypt=True;TrustServerCertificate=True;$extra"
}
$direct = "Server=tcp:$Server;User Id=zoom_tracked;Password=Zoom!Tracked2026;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=True"
$sa = "Server=tcp:$Server;User Id=sa;Password=$SaPassword;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=True"

function Run([string]$connectionString, [string]$sql, [hashtable]$parameters = $null) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 900
        if ($parameters) { foreach ($name in $parameters.Keys) { [void]$cmd.Parameters.AddWithValue($name, $parameters[$name]) } }
        $reader = $cmd.ExecuteReader()
        $lines = New-Object System.Collections.Generic.List[string]
        $values = $null
        do {
            while ($reader.Read()) {
                if ($null -eq $values -or $values.Length -ne $reader.FieldCount) { $values = New-Object object[] $reader.FieldCount }
                [void]$reader.GetValues($values)
                $lines.Add([string]::Join('|', $values))
            }
            $lines.Add('--')
        } while ($reader.NextResult())
        $reader.Close()
        return [pscustomobject]@{ Rows = $lines.ToArray(); Seconds = $clock.Elapsed.TotalSeconds }
    }
    finally { $cn.Close() }
}

function Same($a, $b) { $a.Rows.Count -eq $b.Rows.Count -and (Compare-Object $a.Rows $b.Rows -SyncWindow 0).Count -eq 0 }

# A daffyzoom.* command's answer: its result sets (rows as text, as Run gives them) and its messages.
function Command([string]$connectionString, [string]$sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $messages = New-Object System.Collections.Generic.List[string]
    $cn.add_InfoMessage({ param($sender, $e) foreach ($m in $e.Errors) { $messages.Add($m.Message) } }.GetNewClosure())
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql
        $reader = $cmd.ExecuteReader()
        $sets = New-Object System.Collections.Generic.List[object]
        do {
            $rows = New-Object System.Collections.Generic.List[string]
            $values = New-Object object[] $reader.FieldCount
            while ($reader.Read()) { [void]$reader.GetValues($values); $rows.Add([string]::Join('|', $values)) }
            $sets.Add($rows.ToArray())
        } while ($reader.NextResult())
        $reader.Close()
        return [pscustomobject]@{ Sets = $sets.ToArray(); Messages = $messages.ToArray() }
    }
    finally { $cn.Close() }
}

function SqlError([scriptblock]$action) {
    try { & $action | Out-Null; return $null } catch { $e = $_.Exception; while ($e -and -not ($e -is [System.Data.SqlClient.SqlException])) { $e = $e.InnerException }; return $e }
}

# Three runs: seen slow (and resolved to its tables in the background), filled, served.
function ThreeRuns([string]$connectionString, [string]$sql, [hashtable]$parameters = $null) {
    $runs = @()
    for ($i = 0; $i -lt 3; $i++) {
        $runs += Run $connectionString $sql $parameters
        Start-Sleep -Milliseconds 600        # the analyzer, the resolution and the filler work off the relay
    }
    return $runs
}

function Describe($runs) { ($runs | ForEach-Object { "{0:N2} s" -f $_.Seconds }) -join ', ' }

Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyZoom.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$tracked = (Run $sa "SELECT COUNT(*) FROM sys.change_tracking_tables").Rows[0]
if ([int]$tracked -lt 9) { throw "Change Tracking is not set up on the test database: run docker/seed/tracked.sql (scripts/setup-testdb.ps1 does)" }

$exe = "$root/src/DaffyZoom/bin/$Configuration/net10.0/daffyzoom.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyzoom-tracked-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYZOOM_HOME = "$work\home"
Write-Host "working in $work"

$target = "Server=tcp:$Server;Initial Catalog=$Database;Encrypt=Mandatory;TrustServerCertificate=True"
"tracked-pw`ntracked-pw`n" | & $exe user add analyst --target "$target;User Id=zoom_tracked;Password=Zoom!Tracked2026" | Out-Null
"reports-pw`nreports-pw`n" | & $exe user add reports --target "$target;User Id=zoom_reports;Password=Zoom!Reports2026" | Out-Null
& $exe user admin analyst on | Out-Null
& $exe db set "$Server/$Database" --mode tracked --max-age 4h | Out-Host
Check "the test database is in tracked mode" ($LASTEXITCODE -eq 0)

$proxy = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru `
    -ArgumentList "--port $Port --slow-ms $SlowMs --no-repl --cache-dir `"$work\cache`" --log-file `"$work\daffyzoom.log`"" `
    -RedirectStandardOutput "$work\stdout.txt" -RedirectStandardError "$work\stderr.txt"
$denied = $false
try {
    Check "DaffyZoom listens on $Port" (Wait-Port $Port)
    $analyst = Via analyst tracked-pw

    # ---- served once Change Tracking says nothing changed -----------------------------------------
    $q01 = Get-Content "$queries/q01_category_year.sql" -Raw
    $truth = Run $direct $q01
    $runs = ThreeRuns $analyst $q01
    Check ("q01 is served on its third run in tracked mode ({0}; {1:N1} s direct)" -f (Describe $runs), $truth.Seconds) `
        ($runs[0].Seconds -gt 2 -and $runs[1].Seconds -gt 2 -and $runs[2].Seconds -lt 1)
    Check "q01 from the cache is identical to SQL Server's" ((Same $runs[2] $truth) -and (Same $runs[0] $truth))

    $q02 = Get-Content "$queries/q02_monthly_view.sql" -Raw
    $truth = Run $direct $q02
    $runs = ThreeRuns $analyst $q02
    Check "q02 (a view on a view, a scalar function) resolves to its tables and is served ($(Describe $runs))" `
        ($runs[2].Seconds -lt 1 -and (Same $runs[2] $truth))

    $byYear = "SELECT d.CalendarYear, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales FROM dbo.FactInternetSalesBig f " +
              "JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey WHERE d.CalendarYear >= @fromYear GROUP BY d.CalendarYear ORDER BY 1 OPTION (MAXDOP 1)"
    $runs = ThreeRuns $analyst $byYear @{ '@fromYear' = 2012 }
    Check "a parameterised query is served in tracked mode ($(Describe $runs))" ($runs[2].Seconds -lt 1)

    # ---- changes ---------------------------------------------------------------------------------
    # A row of a table q01 reads, updated to its own values: Change Tracking records it all the same.
    Run $sa "UPDATE dbo.DimProduct SET Color = Color WHERE ProductKey = 1" | Out-Null
    $after = Run $analyst $q01
    Start-Sleep -Milliseconds 600
    $again = Run $analyst $q01
    Check ("a change to a table q01 reads sends it to SQL Server ({0:N1} s), which fills it again: next run {1:N2} s" -f $after.Seconds, $again.Seconds) `
        ($after.Seconds -gt 2 -and $again.Seconds -lt 1)
    $q01Truth = Run $direct $q01
    Check "q01 refilled after the change is identical to SQL Server's" (Same $again $q01Truth)

    Run $sa "INSERT etl.LoadStatus (CompletedUtc) VALUES (SYSUTCDATETIME())" | Out-Null
    $hit = Run $analyst $q01
    Check ("a change to a table q01 does not read leaves it served ({0:N2} s)" -f $hit.Seconds) ($hit.Seconds -lt 1)

    # ---- refused in tracked mode ------------------------------------------------------------------
    $heap = "SELECT ProductKey, COUNT_BIG(*) AS Lines, SUM(SalesAmount) AS Sales FROM dbo.FactResellerSalesBig GROUP BY ProductKey ORDER BY ProductKey OPTION (MAXDOP 1)"
    $runs = ThreeRuns $analyst $heap
    Check "a query on a heap (no Change Tracking) is never served ($(Describe $runs))" (($runs | Where-Object { $_.Seconds -lt 0.5 }).Count -eq 0)

    $dirty = "SELECT COUNT_BIG(*) AS Lines, SUM(SalesAmount) AS Sales FROM dbo.FactInternetSalesBig WITH (NOLOCK) WHERE OrderDateKey >= 20120101 OPTION (MAXDOP 1)"
    $runs = ThreeRuns $analyst $dirty
    Check "a dirty read (NOLOCK) is never served in tracked mode ($(Describe $runs))" (($runs | Where-Object { $_.Seconds -lt 0.5 }).Count -eq 0)

    $reports = Via reports reports-pw
    $runs = ThreeRuns $reports $q01
    Check "a login without the tracked grants is never served ($(Describe $runs))" (($runs | Where-Object { $_.Seconds -lt 1 }).Count -eq 0)

    $watch = (Run $analyst "EXEC daffyzoom.watchlist 50").Rows
    Check "the watch list says the heap has no Change Tracking" `
        (@($watch | Where-Object { $_ -match 'NotTracked: .*FactResellerSalesBig\] is a table without Change Tracking' }).Count -ge 1) ($watch -join "`n")
    Check "the watch list says the dirty read is refused, and why" `
        (@($watch | Where-Object { $_ -match "refused: DirtyRead: it may read uncommitted rows" }).Count -eq 1) ($watch -join "`n")
    Check "the watch list says what the login without the grants lacks" `
        (@($watch | Where-Object { $_ -match 'NotTracked: the login lacks VIEW DEFINITION' -and $_ -match '\|zoom_reports\|' }).Count -eq 1) ($watch -join "`n")

    # ---- access denied after the answer was cached -------------------------------------------------
    $hit = Run $analyst $q01
    Run $sa "DENY SELECT ON dbo.DimProduct TO zoom_tracked" | Out-Null
    $denied = $true
    $error229 = SqlError { Run $analyst $q01 }
    Run $sa "REVOKE SELECT ON dbo.DimProduct TO zoom_tracked" | Out-Null
    $denied = $false
    Check ("access denied after q01 was cached (served in {0:N2} s just before) ends in SQL Server's own error" -f $hit.Seconds) `
        ($hit.Seconds -lt 1 -and $error229 -and $error229.Number -eq 229) "$($error229.Number) $($error229.Message)"

    # ---- what DaffyZoom says -----------------------------------------------------------------------
    $policy = (Run $analyst "EXEC daffyzoom.databases").Rows | Where-Object { $_ -match "\|$Database\|tracked\|" } | Select-Object -First 1
    Check "daffyzoom.databases says the database is tracked and serving" ($policy -match '\|tracked\|serving\|') $policy

    # daffyzoom.tracking, as each login sees it. Database row: server|database|change_tracking|current_version|
    # daffyzoom_last_saw|retention|auto_cleanup|daffyzoom_mode|login|database_user|view_definition|
    # row_level_security|tables_tracked|tables_readable|entries.
    $tracking = Command $analyst "EXEC daffyzoom.tracking"
    $db = $tracking.Sets[0][0].Split('|')
    Check "daffyzoom.tracking: Change Tracking on in $Database, as zoom_tracked sees it, with all $($db[12]) tracked tables readable" `
        ($db[1] -eq $Database -and $db[2] -eq 'on' -and $db[7] -eq 'tracked' -and $db[10] -eq 'yes' -and [int]$db[12] -ge 9 -and $db[12] -eq $db[13]) ($tracking.Sets[0] -join "`n")
    Check "daffyzoom.tracking lists DimProduct as tracked and readable" `
        (@($tracking.Sets[1] | Where-Object { $_ -match '^\[dbo\]\.\[DimProduct\]\|yes\|yes\|\d+\|yes\|yes\|\d+\|\d*\|ok$' }).Count -eq 1) ($tracking.Sets[1] -join "`n")
    Check "daffyzoom.tracking counts the cached entries that depend on each table, with the version they were filled at" `
        ([int]$db[14] -ge 1 -and @($tracking.Sets[1] | Where-Object { $_ -match '\|yes\|yes\|[1-9]\d*\|\d+\|ok$' }).Count -ge 1) ($tracking.Sets[1] -join "`n")
    Check "daffyzoom.tracking says zoom_tracked can use tracked mode" `
        (@($tracking.Messages | Where-Object { $_ -match 'zoom_tracked can use tracked mode' }).Count -eq 1) ($tracking.Messages -join "`n")

    $all = Command $analyst "EXEC daffyzoom.tracking '$Database', 'all'"
    Check "daffyzoom.tracking 'all' shows the heap untracked, and that it needs a primary key first" `
        (@($all.Sets[1] | Where-Object { $_ -match '^\[dbo\]\.\[FactResellerSalesBig\]\|no\|no\|.*needs a primary key' }).Count -eq 1) ($all.Sets[1] -join "`n")

    $lacking = Command $reports "EXEC daffyzoom.tracking"
    Check "daffyzoom.tracking as zoom_reports, without the grants, says what it cannot see and gives the GRANTs" `
        ((@($lacking.Messages | Where-Object { $_ -match 'GRANT VIEW DEFINITION TO \[zoom_reports\];' }).Count -eq 1) -and
         (@($lacking.Messages | Where-Object { $_ -match 'GRANT VIEW CHANGE TRACKING ON SCHEMA::\[dbo\] TO \[zoom_reports\];' }).Count -eq 1) -and
         $lacking.Sets[0][0] -match '\|zoom_reports\|zoom_reports\|no\|cannot tell\|') ($lacking.Messages -join "`n")

    $health = (Run $analyst "EXEC daffyzoom.health").Rows[0]
    Write-Host "  health: $health"
}
finally {
    if ($denied) { try { Run $sa "REVOKE SELECT ON dbo.DimProduct TO zoom_tracked" | Out-Null } catch { } }
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}

$log = Get-Content "$work\daffyzoom.log" -ErrorAction SilentlyContinue
$errors = @($log | Where-Object { $_ -match '"level":"error"' })
Check "DaffyZoom logged no errors" ($errors.Count -eq 0) ($errors -join "`n")

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Remove-Item Env:\DAFFYZOOM_HOME

if ($script:failures -gt 0) { Write-Host "$script:failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed" -ForegroundColor Green
