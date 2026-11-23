<#
.SYNOPSIS
    A throwaway DaffyZoom for trying clients by hand (SSMS, DBeaver, VS Code, Power BI ...)
    against the test server, with the prompt open to watch what it does.

.DESCRIPTION
    Builds DaffyZoom, maps one login - tester, password tester-pw - in a temporary config
    directory, gives the test database a policy, and runs DaffyZoom in this window. Everything it
    made is removed when it stops (:quit, or Ctrl+C).

      (default)   ttl mode on port 14410: tester is the test server's zoom_reports, which only reads.
      -Tracked    tracked mode on port 14411: tester is zoom_tracked, which also has the grants
                  tracked mode needs (VIEW CHANGE TRACKING, VIEW DEFINITION). Every hit first asks
                  SQL Server's Change Tracking whether a table the query reads has changed.

    The two can run side by side. At the prompt: :status (hits, fills, fallbacks, memory),
    :watchlist (slow queries, and why one is not cached), :entries (what is cached), :sessions,
    :log. Or the same from the client: EXEC daffyzoom.health, EXEC daffyzoom.watchlist,
    EXEC daffyzoom.entries.

    Needs the test server from scripts/setup-testdb.ps1.
#>
param(
    [switch]$Tracked,
    [int]$Port = 0,
    [string]$Server = "127.0.0.1,14340",
    [string]$Database = "AdventureWorksDW2022",
    [int]$SlowMs = 1000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Port -eq 0) { $Port = if ($Tracked) { 14411 } else { 14410 } }
$mode = if ($Tracked) { 'tracked' } else { 'ttl' }
$mapped = if ($Tracked) { 'User Id=zoom_tracked;Password=Zoom!Tracked2026' } else { 'User Id=zoom_reports;Password=Zoom!Reports2026' }

Write-Host "building..."
dotnet build "$root/DaffyZoom.slnx" -c Debug -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = "$root/src/DaffyZoom/bin/Debug/net10.0/daffyzoom.exe"

$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyzoom-try-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYZOOM_HOME = "$work\home"
try {
    $target = "Server=tcp:$Server;Initial Catalog=$Database;Encrypt=Mandatory;TrustServerCertificate=True;$mapped"
    "tester-pw`ntester-pw`n" | & $exe user add tester --target $target | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not map tester: is the test server up?" }
    & $exe user admin tester on | Out-Null
    & $exe db set "$Server/$Database" --mode $mode --max-age 4h | Out-Null

    Write-Host ""
    Write-Host "  Mode         $mode"
    Write-Host "  Connect to   127.0.0.1,$Port   (SQL Server authentication)"
    Write-Host "  Login        tester"
    Write-Host "  Password     tester-pw"
    Write-Host "  Encryption   Mandatory, and tick Trust server certificate"
    Write-Host "  Database     $Database"
    Write-Host ""
    Write-Host "  A query taking over $SlowMs ms is cached on its second run and served on its third."
    if ($Tracked) { Write-Host "  Each hit asks Change Tracking first: after a change to a table it reads, the query goes to SQL Server once." }
    Write-Host "  :status shows hits; :watchlist says why a query is not cached; :quit stops and cleans up."
    Write-Host ""

    & $exe --port $Port --slow-ms $SlowMs --cache-dir "$work\cache" --log-file "$work\daffyzoom.log"
}
finally {
    Remove-Item Env:\DAFFYZOOM_HOME -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
