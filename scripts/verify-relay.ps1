<#
.SYNOPSIS
    End-to-end check of DaffyZoom as a gateway: a real DaffyZoom, a real client, the real test
    SQL Server. Phase 0: everything must be relayed exactly; nothing is cached yet.

.DESCRIPTION
    Builds the solution, creates mappings in a throwaway config directory (answering the prompts
    through stdin, as any script would), starts DaffyZoom on its own port, and checks through it:
    identity pass-through, results identical to a direct connection (a small query and a ~5 s one),
    refused logins, the daffyzoom.* commands for an admin and a non-admin, pooled connections
    (reset flag), and MARS.

    The client is the System.Data.SqlClient built into Windows PowerShell 5.1 (and pwsh), so no
    probe project is needed. It needs the test server from scripts/setup-testdb.ps1, and starts and
    stops its own DaffyZoom, so one already running is not disturbed.

    Asserts on what the run must produce, never merely on the absence of an error, which a run
    that never connected would also satisfy.
#>
param(
    [int]$Port = 14401,
    [string]$Target = "Server=tcp:127.0.0.1,14340;User Id=zoom_reports;Password=Zoom!Reports2026;Initial Catalog=AdventureWorksDW2022;Encrypt=Mandatory;TrustServerCertificate=True",
    [string]$DirectConnection = "Server=tcp:127.0.0.1,14340;User Id=zoom_reports;Password=Zoom!Reports2026;Initial Catalog=AdventureWorksDW2022;Encrypt=True;TrustServerCertificate=True",
    [string]$Configuration = "Debug",
    [int]$SlowMs = 1000,
    [switch]$SkipSlow
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
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

# Runs a query and returns its rows as text, one line per row: what is compared between paths.
# With $parameters (a hashtable), SqlClient sends it as sp_executesql, as an application would.
function Rows([string]$connectionString, [string]$sql, [int]$timeout = 600, [hashtable]$parameters = $null) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = $timeout
        if ($parameters) { foreach ($name in $parameters.Keys) { [void]$cmd.Parameters.AddWithValue($name, $parameters[$name]) } }
        $reader = $cmd.ExecuteReader()
        $lines = New-Object System.Collections.Generic.List[string]
        do {
            while ($reader.Read()) {
                $values = New-Object object[] $reader.FieldCount
                [void]$reader.GetValues($values)
                $lines.Add((($values | ForEach-Object { if ($_ -is [DBNull]) { 'NULL' } else { [string]$_ } }) -join '|'))
            }
            $lines.Add('--')
        } while ($reader.NextResult())
        $reader.Close()
        return ,$lines.ToArray()
    }
    finally { $cn.Close() }
}

function SqlError([scriptblock]$action) {
    try { & $action; return $null } catch { $e = $_.Exception; while ($e -and -not ($e -is [System.Data.SqlClient.SqlException])) { $e = $e.InnerException }; return $e }
}

Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyZoom.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = "$root/src/DaffyZoom/bin/$Configuration/net10.0/daffyzoom.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyzoom-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYZOOM_HOME = "$work\home"
Write-Host "working in $work"

# Mappings, made the way a script makes them: the answers piped to the prompts.
"verify-password`nverify-password`n" | & $exe user add verify --target $Target | Out-Host
Check "user add validates the target and saves" ($LASTEXITCODE -eq 0)

"x`nx`n" | & $exe user add broken --target ($Target -replace 'Password=[^;]*', 'Password=wrong') | Out-Null
Check "user add refuses a target that will not log in" ($LASTEXITCODE -ne 0)

"plain-password`nplain-password`n" | & $exe user add plain --target $Target | Out-Null
& $exe user admin verify on | Out-Null

$proxy = Start-Process -FilePath $exe -ArgumentList "--port $Port --slow-ms $SlowMs --no-repl --log-file `"$work\daffyzoom.log`"" `
    -RedirectStandardOutput "$work\stdout.txt" -RedirectStandardError "$work\stderr.txt" -PassThru -WindowStyle Hidden
try {
    Check "DaffyZoom listens on $Port" (Wait-Port $Port)

    $who = Rows (Via verify verify-password) "SELECT SUSER_SNAME(), DB_NAME()"
    Check "the session runs on the target as the mapped login" ($who[0] -eq 'zoom_reports|AdventureWorksDW2022') $who[0]

    $small = "SELECT ProductKey, EnglishProductName, ListPrice, StartDate, Color FROM dbo.DimProduct ORDER BY ProductKey"
    $relayed = Rows (Via verify verify-password) $small
    $directRows = Rows $DirectConnection $small
    Check "a small result is identical relayed and direct ($($directRows.Count - 1) rows)" ((Compare-Object $relayed $directRows -SyncWindow 0).Count -eq 0)

    $several = "SELECT TOP (3) CustomerKey FROM dbo.DimCustomer ORDER BY CustomerKey; PRINT 'between'; SELECT COUNT_BIG(*) FROM dbo.DimDate"
    Check "a batch with several result sets is identical" ((Compare-Object (Rows (Via verify verify-password) $several) (Rows $DirectConnection $several) -SyncWindow 0).Count -eq 0)

    if (-not $SkipSlow) {
        $q01 = Get-Content "$root/docker/queries/q01_category_year.sql" -Raw
        $clock = [Diagnostics.Stopwatch]::StartNew(); $directSlow = Rows $DirectConnection $q01; $directSeconds = $clock.Elapsed.TotalSeconds
        $clock.Restart(); $relayedSlow = Rows (Via verify verify-password) $q01; $relayedSeconds = $clock.Elapsed.TotalSeconds
        Check ("a slow query is identical relayed and direct ({0:N1} s direct, {1:N1} s relayed)" -f $directSeconds, $relayedSeconds) `
            ((Compare-Object $relayedSlow $directSlow -SyncWindow 0).Count -eq 0)

        # Phase 1: what the cache would do. Two more runs of the same query: the second slow run
        # would fill, the third would be served. A parameterised query three times, the same way.
        # A query on the clock, refused.
        Rows (Via verify verify-password) $q01 | Out-Null
        Rows (Via verify verify-password) $q01 | Out-Null
        $byYear = "SELECT d.CalendarYear, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales FROM dbo.FactInternetSalesBig f " +
                  "JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey WHERE d.CalendarYear >= @fromYear GROUP BY d.CalendarYear ORDER BY 1 OPTION (MAXDOP 1)"
        for ($i = 0; $i -lt 3; $i++) { Rows (Via verify verify-password) $byYear -parameters @{ '@fromYear' = 2011 } | Out-Null }
        Rows (Via verify verify-password) (Get-Content "$root/docker/queries/r01_getdate.sql" -Raw) | Out-Null
        Start-Sleep -Milliseconds 500   # the analyzer works off the relay; give it a moment

        $watch = Rows (Via verify verify-password) "EXEC daffyzoom.watchlist 50"
        # "would serve", and with no policy on the database (as here) the db set line that would cache it.
        $served = @($watch | Where-Object { $_ -match '^\w+\|would serve[|;]' })
        Check "the watch list says both repeated queries would be served ($($served.Count))" ($served.Count -ge 2) ($watch -join "`n")
        Check "the parameterised one is there by its shape" (@($watch | Where-Object { $_ -match 'where d \. calendaryear > ?= @fromyear' -and $_ -match '\|would serve[|;]' }).Count -eq 1)
        Check "the query on the clock is refused, saying why" (@($watch | Where-Object { $_ -match '\|refused: NonDeterministic: GETDATE\(\)\|' }).Count -eq 1)

        $health = Rows (Via verify verify-password) "EXEC daffyzoom.health"
        Check "daffyzoom.health counts what the cache saw" ($health[0] -match '\|observing only\|') $health[0]
    }

    $bad = SqlError { Rows (Via verify wrong-password) "SELECT 1" | Out-Null }
    Check "a wrong DaffyZoom password is refused with 18456" ($bad -and $bad.Number -eq 18456) "$($bad.Number) $($bad.Message)"

    $unknown = SqlError { Rows (Via nobody whatever) "SELECT 1" | Out-Null }
    Check "an unknown login is refused with 18456" ($unknown -and $unknown.Number -eq 18456) "$($unknown.Number)"

    $status = Rows (Via verify verify-password) "EXEC daffyzoom.status"
    Check "daffyzoom.status is answered by DaffyZoom" ($status[0] -match '^\d+\|verify\|zoom_reports\|') $status[0]

    $refused = SqlError { Rows (Via plain plain-password) "EXEC daffyzoom.sessions" | Out-Null }
    Check "a non-admin is refused daffyzoom.sessions with 50303" ($refused -and $refused.Number -eq 50303) "$($refused.Number)"

    # Pooled: the same physical connection is handed out again, carrying the reset flag each time.
    $pooled = Via verify verify-password "Pooling=True;Max Pool Size=2"
    $ok = 0
    for ($i = 0; $i -lt 25; $i++) {
        if ((Rows $pooled "SELECT DB_NAME()")[0] -eq 'AdventureWorksDW2022') { $ok++ }
        if ($i -eq 3) { Rows $pooled "USE master; SELECT 1" | Out-Null }   # the reset must bring the database back
    }
    Check "25 pooled uses see their own database after a reset ($ok of 25)" ($ok -eq 25)

    $mars = Rows (Via verify verify-password "MultipleActiveResultSets=True") "SELECT COUNT_BIG(*) FROM dbo.DimProduct"
    Check "a MARS session is relayed" ($mars[0] -eq (Rows $DirectConnection "SELECT COUNT_BIG(*) FROM dbo.DimProduct")[0])
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}

$log = Get-Content "$work\daffyzoom.log" -ErrorAction SilentlyContinue
Check "the log records sessions opening" (@($log | Where-Object { $_ -match '"event":"session.open"' }).Count -ge 1)
Check "the log names the refused logins" (@($log | Where-Object { $_ -match '"event":"session.login_refused"' }).Count -ge 2)

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Remove-Item Env:\DAFFYZOOM_HOME

if ($script:failures -gt 0) { Write-Host "$script:failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed" -ForegroundColor Green
