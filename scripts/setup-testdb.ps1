<#
.SYNOPSIS
    Builds DaffyZoom's test SQL Server: downloads AdventureWorksDW2022, starts the container,
    restores the database and inflates it to tens of millions of rows. Safe to run again: every
    step that already happened is skipped.

.DESCRIPTION
    Runs under Windows PowerShell 5.1 or pwsh, from anywhere in the repository. Takes a few minutes
    the first time, mostly the inflate. Afterwards the container restarts with Docker and keeps its
    data; run this again only after "docker compose down -v", or to finish an interrupted build.
#>
[CmdletBinding()]
param(
    [string]$SaPassword = $(if ($env:MSSQL_SA_PASSWORD) { $env:MSSQL_SA_PASSWORD } else { 'DaffyZoom!Dev2026' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $root 'docker/compose.yml'
$backups = Join-Path $root 'docker/backups'
$backup = Join-Path $backups 'AdventureWorksDW2022.bak'
$backupUrl = 'https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorksDW2022.bak'
$container = 'daffyzoom-sql-1'

function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }

function Invoke-Sql([string]$file) {
    & docker exec $container /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $SaPassword -b -i $file
    if ($LASTEXITCODE -ne 0) { throw "$file failed (sqlcmd exit $LASTEXITCODE)" }
}

Step 'backup'
New-Item -ItemType Directory -Force $backups | Out-Null
if (-not (Test-Path $backup)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $partial = "$backup.partial"
    Write-Host "downloading $backupUrl"
    Invoke-WebRequest -Uri $backupUrl -OutFile $partial -UseBasicParsing
    Move-Item $partial $backup -Force
}
Write-Host ("{0} ({1:N1} MB)" -f $backup, ((Get-Item $backup).Length / 1MB))

Step 'container'
& docker compose -f $compose up -d
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed' }

$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 3
    $health = & docker inspect --format '{{.State.Health.Status}}' $container
    Write-Host "  $container is $health"
} until ($health -eq 'healthy' -or (Get-Date) -gt $deadline)
if ($health -ne 'healthy') { throw "$container did not become healthy within 3 minutes" }

Step 'restore and logins'
Invoke-Sql '/seed/restore.sql'

Step 'inflate (several minutes the first time)'
$clock = [Diagnostics.Stopwatch]::StartNew()
Invoke-Sql '/seed/inflate.sql'
Write-Host ("inflate finished in {0:N0} s" -f $clock.Elapsed.TotalSeconds)

Step 'fixtures'
Invoke-Sql '/seed/fixtures.sql'

Step 'change tracking (tracked mode)'
Invoke-Sql '/seed/tracked.sql'

Step 'ready'
Write-Host "SQL Server: tcp:127.0.0.1,14340  sa / $('*' * $SaPassword.Length)"
Write-Host "logins: zoom_reports / Zoom!Reports2026, zoom_tracked / Zoom!Tracked2026, zoom_limited / Zoom!Limited2026"
