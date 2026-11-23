<#
.SYNOPSIS
    End-to-end check of serving from the cache: a real DaffyZoom in ttl mode, a real client, the
    real test SQL Server with its slow queries.

.DESCRIPTION
    Builds the solution, maps three logins in a throwaway config directory, gives the test database
    a ttl policy, starts DaffyZoom on its own port with a throwaway cache directory, and checks:

      - a slow query's third run is answered from the cache, much faster, identical to SQL Server's
      - the same for a 500,000-row answer, replayed at a different packet size
      - a parameterised query (sp_executesql) is cached the same way
      - a batch's leading SETs are re-applied on a hit: the session is left as if it had run
      - a query on the clock (GETDATE) is never cached
      - a write through DaffyZoom flushes; so does daffyzoom.flush from a non-admin (an ETL job)
      - the kill switch sends everything to SQL Server
      - a login without access to the tables is never served another login's answer
      - daffyzoom.entries, daffyzoom.watchlist and daffyzoom.health say what happened

    With -Long it adds q10 (about 70 s, run twice, then served).

    The client is the System.Data.SqlClient built into Windows PowerShell; its pooled connections
    reset between uses, as an application's do. It needs the test server from
    scripts/setup-testdb.ps1, and leaves its data as it found it apart from rows in etl.LoadStatus.
#>
param(
    [int]$Port = 14403,
    [string]$Server = "127.0.0.1,14340",
    [string]$Database = "AdventureWorksDW2022",
    [string]$Configuration = "Debug",
    [int]$SlowMs = 1000,
    [switch]$Long
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
$direct = "Server=tcp:$Server;User Id=zoom_reports;Password=Zoom!Reports2026;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=True"

# Runs a query; returns its rows as text (one line per row, result sets separated by --) and the seconds it took.
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

function SqlError([scriptblock]$action) {
    try { & $action | Out-Null; return $null } catch { $e = $_.Exception; while ($e -and -not ($e -is [System.Data.SqlClient.SqlException])) { $e = $e.InnerException }; return $e }
}

# Three runs through DaffyZoom: seen slow, filled, served. Returns the three results.
function ThreeRuns([string]$connectionString, [string]$sql, [hashtable]$parameters = $null) {
    $runs = @()
    for ($i = 0; $i -lt 3; $i++) {
        $runs += Run $connectionString $sql $parameters
        Start-Sleep -Milliseconds 400        # the analyzer and the filler work off the relay
    }
    return $runs
}

function Describe($runs) { ($runs | ForEach-Object { "{0:N2} s" -f $_.Seconds }) -join ', ' }

Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyZoom.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = "$root/src/DaffyZoom/bin/$Configuration/net10.0/daffyzoom.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyzoom-cache-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYZOOM_HOME = "$work\home"
Write-Host "working in $work"

$target = "Server=tcp:$Server;Initial Catalog=$Database;Encrypt=Mandatory;TrustServerCertificate=True"
"reports-pw`nreports-pw`n" | & $exe user add reports --target "$target;User Id=zoom_reports;Password=Zoom!Reports2026" | Out-Null
"etl-pw`netl-pw`n" | & $exe user add etl --target "$target;User Id=zoom_reports;Password=Zoom!Reports2026" | Out-Null
"limited-pw`nlimited-pw`n" | & $exe user add limited --target "$target;User Id=zoom_limited;Password=Zoom!Limited2026" | Out-Null
& $exe user admin reports on | Out-Null
& $exe db set "$Server/$Database" --mode ttl --max-age 4h | Out-Host
Check "the test database has a ttl policy" ($LASTEXITCODE -eq 0)

$proxy = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru `
    -ArgumentList "--port $Port --slow-ms $SlowMs --no-repl --cache-dir `"$work\cache`" --log-file `"$work\daffyzoom.log`"" `
    -RedirectStandardOutput "$work\stdout.txt" -RedirectStandardError "$work\stderr.txt"
try {
    Check "DaffyZoom listens on $Port" (Wait-Port $Port)
    $reports = Via reports reports-pw

    # ---- a slow query, served on its third run ------------------------------------------------
    $q01 = Get-Content "$queries/q01_category_year.sql" -Raw
    $truth = Run $direct $q01
    $runs = ThreeRuns $reports $q01
    Check "q01 runs at SQL Server's pace twice ($(Describe $runs[0..1]))" ($runs[0].Seconds -gt 2 -and $runs[1].Seconds -gt 2)
    Check ("q01's third run is served from the cache in {0:N2} s, against {1:N1} s direct" -f $runs[2].Seconds, $truth.Seconds) ($runs[2].Seconds -lt 1)
    Check "q01 from the cache is identical to SQL Server's ($($truth.Rows.Count - 1) rows)" ((Same $runs[2] $truth) -and (Same $runs[0] $truth))

    # ---- 500,000 rows, replayed at another packet size ----------------------------------------
    $q09 = Get-Content "$queries/q09_big_result.sql" -Raw
    $truth = Run $direct $q09
    $runs = ThreeRuns $reports $q09
    $small = Run (Via reports reports-pw "Packet Size=512") $q09
    Check ("q09's 500,000 rows come from the cache ({0}, then {1:N1} s at packet size 512; {2:N1} s direct)" -f (Describe $runs), $small.Seconds, $truth.Seconds) `
        ($runs[2].Seconds -lt $runs[0].Seconds -and $small.Seconds -lt $runs[0].Seconds)
    Check "q09 from the cache is identical to SQL Server's, at either packet size" ((Same $runs[2] $truth) -and (Same $small $truth))

    # ---- parameterised --------------------------------------------------------------------------
    $byYear = "SELECT d.CalendarYear, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales FROM dbo.FactInternetSalesBig f " +
              "JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey WHERE d.CalendarYear >= @fromYear GROUP BY d.CalendarYear ORDER BY 1 OPTION (MAXDOP 1)"
    $truth = Run $direct $byYear @{ '@fromYear' = 2012 }
    $runs = ThreeRuns $reports $byYear @{ '@fromYear' = 2012 }
    Check "a parameterised query is served from the cache on its third run ($(Describe $runs))" ($runs[2].Seconds -lt 1 -and (Same $runs[2] $truth))
    $other = Run $reports $byYear @{ '@fromYear' = 2013 }
    Check ("another parameter value is not served that answer ({0:N1} s)" -f $other.Seconds) ($other.Seconds -gt 1 -and -not (Same $other $truth))

    # ---- leading SETs: the session is left as if the batch had run --------------------------------
    $r03 = Get-Content "$queries/r03_leading_set.sql" -Raw
    $runs = ThreeRuns $reports $r03
    $cn = New-Object System.Data.SqlClient.SqlConnection $reports
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $r03; $cmd.CommandTimeout = 900
        $clock = [Diagnostics.Stopwatch]::StartNew(); $r = $cmd.ExecuteReader(); while ($r.Read()) { }; $r.Close(); $hitSeconds = $clock.Elapsed.TotalSeconds
        $cmd.CommandText = "SELECT @@OPTIONS & 512, @@OPTIONS & 64"       # NOCOUNT, ARITHABORT
        $r = $cmd.ExecuteReader(); [void]$r.Read(); $nocount = $r.GetInt32(0); $arithabort = $r.GetInt32(1); $r.Close()
    }
    finally { $cn.Close() }
    Check ("a batch with leading SETs is served from the cache ({0}, then {1:N2} s)" -f (Describe $runs), $hitSeconds) ($hitSeconds -lt 1)
    Check "after that hit the server session has NOCOUNT and ARITHABORT on, as the batch set them" ($nocount -eq 512 -and $arithabort -eq 64) "nocount=$nocount arithabort=$arithabort"

    # ---- never cached --------------------------------------------------------------------------
    $r01 = Get-Content "$queries/r01_getdate.sql" -Raw
    $runs = ThreeRuns $reports $r01
    Check "a query on the clock is never served from the cache ($(Describe $runs))" (($runs | Where-Object { $_.Seconds -lt 0.5 }).Count -eq 0)

    # ---- flushes -------------------------------------------------------------------------------
    Run $reports (Get-Content "$queries/r04_write.sql" -Raw) | Out-Null
    $after = Run $reports $q01
    Check ("a write through DaffyZoom flushes the database: q01 goes to SQL Server again ({0:N1} s)" -f $after.Seconds) ($after.Seconds -gt 2)

    $q03 = Get-Content "$queries/q03_customer_value.sql" -Raw
    $runs = ThreeRuns $reports $q03
    Check "q03 is served from the cache on its third run ($(Describe $runs))" ($runs[2].Seconds -lt 1)
    Run (Via etl etl-pw) "EXEC daffyzoom.flush '$Database'" | Out-Null
    $after = Run $reports $q03
    Check ("daffyzoom.flush from a non-admin ETL login flushes it: q03 goes to SQL Server again ({0:N1} s)" -f $after.Seconds) ($after.Seconds -gt 2)

    # ---- the kill switch ---------------------------------------------------------------------------
    Run $reports $q03 | Out-Null; Start-Sleep -Milliseconds 400          # filled again
    Run $reports "EXEC daffyzoom.set_serving 0" | Out-Null
    $off = Run $reports $q03
    Run $reports "EXEC daffyzoom.set_serving 1" | Out-Null
    $on = Run $reports $q03
    Check ("with serving off q03 goes to SQL Server ({0:N1} s); back on, it is served ({1:N2} s)" -f $off.Seconds, $on.Seconds) ($off.Seconds -gt 2 -and $on.Seconds -lt 1)

    # ---- another login is never served what it may not read -----------------------------------------
    $denied = SqlError { Run (Via limited limited-pw) $q03 }
    Check "a login without access to the tables gets SQL Server's own permission error, not the cached answer" ($denied -and $denied.Number -eq 229) "$($denied.Number) $($denied.Message)"

    # ---- policies changed while DaffyZoom runs, and signals from outside it -------------------------
    # The parameterised query (about 4 s) stands in: the watch list knows it, so one run refills it.
    $p2012 = @{ '@fromYear' = 2012 }
    function Policy() { (Run $reports "EXEC daffyzoom.databases").Rows | Where-Object { $_ -match "\|$Database\|ttl\|" } | Select-Object -First 1 }
    # The running DaffyZoom rereads the policies each second, so straight after a db set it may still
    # answer with the old one: $policy (matched against the policy's description) says it is the new one.
    function Wait-State([string]$pattern, [string]$policy = '', [int]$seconds = 30) {
        $deadline = (Get-Date).AddSeconds($seconds)
        do {
            $row = (Policy).Split('|'); $state = $row[3]
            if ($state -match $pattern -and $row[6] -match $policy) { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        return $state
    }
    function Refill() { Run $reports $byYear $p2012 | Out-Null; Start-Sleep -Milliseconds 400; return Run $reports $byYear $p2012 }

    $hit = Refill
    & $exe db set "$Server/$Database" --idle 2h | Out-Null
    Start-Sleep -Milliseconds 1500                                        # the running DaffyZoom rereads the store each second
    $after = Run $reports $byYear $p2012
    Check ("a policy changed from the command line ends what was filled under the old one ({0:N2} s, then {1:N1} s)" -f $hit.Seconds, $after.Seconds) `
        ($hit.Seconds -lt 1 -and $after.Seconds -gt 1)

    # A watermark: read within seconds of being added, then any change to its answer flushes - here a
    # load that goes straight to SQL Server, which DaffyZoom never sees.
    & $exe db set "$Server/$Database" --watermark "SELECT MAX(LoadId) FROM etl.LoadStatus" --watermark-as etl --every 5s | Out-Null
    $state = Wait-State '^serving$' 'watermark every 5s'
    Check "a watermark added while DaffyZoom runs is read within seconds, and serving starts" ($state -eq 'serving') $state
    $hit = Refill
    Check ("with the watermark unchanged, the query is served ({0:N2} s)" -f $hit.Seconds) ($hit.Seconds -lt 1)
    Run $direct "INSERT etl.LoadStatus (CompletedUtc) VALUES (SYSUTCDATETIME())" | Out-Null
    $deadline = (Get-Date).AddSeconds(25)
    do { Start-Sleep -Milliseconds 500; $live = [int](Policy).Split('|')[7] } while ($live -gt 0 -and (Get-Date) -lt $deadline)
    $after = Run $reports $byYear $p2012
    Check ("a load straight to SQL Server changes the watermark, which flushes: the query goes to SQL Server again ({0:N1} s)" -f $after.Seconds) `
        ($live -eq 0 -and $after.Seconds -gt 1)

    # A watermark that cannot be read: nothing is served, and daffyzoom.databases says why.
    & $exe db set "$Server/$Database" --watermark "SELECT MAX(LoadId) FROM etl.NoSuchTable" | Out-Null
    $state = Wait-State 'not served: its watermark query failed'
    $runs = @((Run $reports $byYear $p2012), (Run $reports $byYear $p2012))
    Check "while its watermark fails, nothing is served ($(Describe $runs)), and daffyzoom.databases says why" `
        ($state -match 'Invalid object name' -and $runs[1].Seconds -gt 1) $state

    # A blackout window around now: nothing is served or filled.
    $from = (Get-Date).AddMinutes(-1).ToString('HH:mm'); $to = (Get-Date).AddMinutes(10).ToString('HH:mm')
    & $exe db set "$Server/$Database" --watermark none --blackout "$from-$to" | Out-Null
    $state = Wait-State 'blackout'
    $runs = @((Run $reports $byYear $p2012), (Run $reports $byYear $p2012))
    Check "in a blackout window ($from-$to) nothing is served ($(Describe $runs))" ($state -match 'blackout' -and $runs[1].Seconds -gt 1) $state
    & $exe db set "$Server/$Database" --blackout none | Out-Null
    $state = Wait-State '^serving$' '^(?!.*blackout)'
    $hit = Refill
    Check ("with the blackout cleared the query is filled and served again ({0:N2} s)" -f $hit.Seconds) ($state -eq 'serving' -and $hit.Seconds -lt 1) $state

    # ---- a cancel part-way through a 500,000-row replay ------------------------------------------------
    # The replay runs to its end, then the attention goes to SQL Server, which acknowledges it; the
    # pooled connection carries on.
    $dimDate = (Run $direct "SELECT COUNT_BIG(*) FROM dbo.DimDate").Rows[0]
    $pooled = Via reports reports-pw "Application Name=zoom-cancel"
    Run $pooled $q09 | Out-Null; Start-Sleep -Milliseconds 400            # filled again (the policy changes ended it)
    $cn = New-Object System.Data.SqlClient.SqlConnection $pooled
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $q09; $cmd.CommandTimeout = 900
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $r = $cmd.ExecuteReader(); $read = 0; while ($read -lt 1000 -and $r.Read()) { $read++ }
        $cmd.Cancel(); $r.Close(); $cancelSeconds = $clock.Elapsed.TotalSeconds
        $cmd.CommandText = "SELECT DB_NAME(), COUNT_BIG(*) FROM dbo.DimDate"
        $r = $cmd.ExecuteReader(); [void]$r.Read(); $afterCancel = "$($r.GetString(0))|$($r.GetInt64(1))"; $r.Close()
    }
    finally { $cn.Close() }
    $again = Run $pooled "SELECT COUNT_BIG(*) FROM dbo.DimDate"
    Check ("a cancel part-way through a cached replay returns ({0:N1} s), and the connection carries on" -f $cancelSeconds) `
        ($cancelSeconds -lt 15 -and $afterCancel -eq "$Database|$dimDate" -and $again.Rows[0] -eq $dimDate) "$afterCancel / $($again.Rows[0])"

    # q03 is filled again for what follows (the policy changes ended it).
    Run $reports $q03 | Out-Null; Start-Sleep -Milliseconds 400

    if ($Long) {
        $q10 = Get-Content "$queries/q10_internet_medians.sql" -Raw
        $runs = ThreeRuns $reports $q10
        Check "q10 ($(Describe $runs)): minutes at SQL Server, then from the cache" ($runs[2].Seconds -lt 1 -and $runs[0].Seconds -gt 30)
    }

    # ---- what DaffyZoom says about it ------------------------------------------------------------
    # The write and the ETL flush left only q03 (filled again after the flush) cached and live.
    $q03Shape = 'count \( DISTINCT f \. salesordernumber \)'
    $entries = @((Run $reports "EXEC daffyzoom.entries 50").Rows | Where-Object { $_ -ne '--' })
    Check "daffyzoom.entries lists what is live in the cache: q03, served ($($entries.Count) entries)" `
        (@($entries | Where-Object { $_ -match $q03Shape }).Count -eq 1) ($entries -join "`n")
    $watch = (Run $reports "EXEC daffyzoom.watchlist 50").Rows
    Check "daffyzoom.watchlist marks q03 cached and the query on the clock refused" `
        ((@($watch | Where-Object { $_ -match '^\w+\|cached\|' -and $_ -match $q03Shape }).Count -eq 1) -and
         (@($watch | Where-Object { $_ -match '\|refused: NonDeterministic' }).Count -eq 1)) ($watch -join "`n")
    $health = (Run $reports "EXEC daffyzoom.health").Rows[0]
    Check "daffyzoom.health counts hits and fills" ($health -match '\|serving\|') $health
    Write-Host "  health: $health"
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}

$log = Get-Content "$work\daffyzoom.log" -ErrorAction SilentlyContinue
$errors = @($log | Where-Object { $_ -match '"level":"error"' })
Check "DaffyZoom logged no errors" ($errors.Count -eq 0) ($errors -join "`n")

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Remove-Item Env:\DAFFYZOOM_HOME

if ($script:failures -gt 0) { Write-Host "$script:failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed" -ForegroundColor Green
