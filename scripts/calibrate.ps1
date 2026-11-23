<#
.SYNOPSIS
    Times every docker/queries/q*.sql against the test SQL Server, directly (not through DaffyZoom),
    and writes what they take on this machine to docker/queries/calibration-<machine>.txt.

.DESCRIPTION
    Each query runs twice: cold (the first run after the container started, or after other queries
    pushed its data out of memory) and warm. The warm figure is the fair comparison for a cache hit:
    it is what SQL Server takes when it has every advantage. Output rows are discarded inside the
    container, so client-side formatting does not count.

    The container is capped at 4 CPUs and 6 GB, so these times are much longer than the same
    queries on an uncapped container. Run nothing else heavy against it while this runs.
#>
[CmdletBinding()]
param(
    [string]$SaPassword = $(if ($env:MSSQL_SA_PASSWORD) { $env:MSSQL_SA_PASSWORD } else { 'DaffyZoom!Dev2026' }),
    [string]$Filter = 'q*.sql',
    [int]$Runs = 2
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$queries = Join-Path $root 'docker/queries'
$out = Join-Path $queries ("calibration-{0}.txt" -f $env:COMPUTERNAME.ToLowerInvariant())
$container = 'daffyzoom-sql-1'

$lines = @("# DaffyZoom query calibration on $env:COMPUTERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
           "# container capped at 4 CPUs / 6 GB; seconds per run, run 1 first",
           "")
foreach ($file in Get-ChildItem $queries -Filter $Filter | Sort-Object Name) {
    $times = @()
    for ($i = 1; $i -le $Runs; $i++) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        & docker exec $container /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $SaPassword `
            -d AdventureWorksDW2022 -b -i "/queries/$($file.Name)" -o /dev/null
        $code = $LASTEXITCODE
        $clock.Stop()
        if ($code -ne 0) { throw "$($file.Name) failed (sqlcmd exit $code)" }
        $times += [math]::Round($clock.Elapsed.TotalSeconds, 1)
    }
    $line = "{0,-28} {1}" -f $file.Name, (($times | ForEach-Object { "{0,7:N1}" -f $_ }) -join ' ')
    Write-Host $line
    $lines += $line
}

Set-Content -Path $out -Value $lines -Encoding utf8
Write-Host "written to $out"
