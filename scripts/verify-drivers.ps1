<#
.SYNOPSIS
    End-to-end check of DaffyZoom with the SQL Server drivers applications use: each one connects
    through a real DaffyZoom in ttl mode, runs a slow query until it is served from the cache, and
    must see what SQL Server would have sent.

.DESCRIPTION
    For every driver found on this machine (see -Only for the names), through DaffyZoom:

      who         it logs in, and runs as the mapped target login
      batch1..3   a slow SQL batch: the first two runs at SQL Server's pace, the third served from
                  the cache, identical to the first as this driver decodes it
      state       the session's options, before and after that hit on the same connection, the same
      param1..3   a slow parameterised query, likewise; paramOther, another value, not served that answer

    and driver by driver what else it does differently (tedious's sp_executesql without parameters,
    JDBC's prepared statement reused by handle). Drivers that cannot be served - MARS, TDS older
    than 7.3 - must get every answer from SQL Server, identical run to run. Each query carries the
    driver's name in a comment, so no driver is served another's entry. legacy-logins: the two
    TDS 7.1 drivers built into Windows with a long login, a wrong password, a USE and Encrypt=yes.

    Served means DaffyZoom's own hit counter (daffyzoom.health) moved; the timings must agree.
    At the end: a table of each driver's TDS version and encryption as DaffyZoom logged them.

    Drivers in-process (Windows PowerShell): System.Data.SqlClient, OLE DB (MSOLEDBSQL19,
    SQLOLEDB). Driver programs in scripts/drivers: probe.py (pyodbc: ODBC 18, 17, the Windows
    'SQL Server' driver; pymssql: FreeTDS), Probe.java (mssql-jdbc), probe.js (tedious), mds/
    (Microsoft.Data.SqlClient). Command lines: sqlcmd (ODBC) and the newer Go sqlcmd.

    Python packages and tedious are installed into -ToolsDir on first use (a venv, and npm
    --ignore-scripts); Java and the JDBC jar are found on PATH or in DBeaver's folders. Probe.java
    is compiled by the javac beside that java, or, for a runtime without one (DBeaver's), by
    Eclipse's compiler (ecj), fetched once from Maven Central and checked against its published
    SHA-1. Whatever is missing is skipped, and said so. Needs the test server from
    scripts/setup-testdb.ps1.
#>
param(
    [int]$Port = 14406,
    [string]$Server = "127.0.0.1,14340",
    [string]$Database = "AdventureWorksDW2022",
    [string]$Configuration = "Debug",
    [int]$SlowMs = 800,
    [string]$ToolsDir = (Join-Path ([IO.Path]::GetTempPath()) 'daffyzoom-probe-tools'),
    [string]$JdbcJar = "",
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$drivers = Join-Path $PSScriptRoot 'drivers'
$script:failures = 0
$script:skipped = New-Object System.Collections.Generic.List[string]
$script:matrix = New-Object System.Collections.Generic.List[object]

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

function Wanted([string]$variant) { $Only.Count -eq 0 -or $Only -contains $variant }

# ---- the queries ------------------------------------------------------------------------------------
# Slow enough to count (above -SlowMs on this test server), quick enough to run many times; their
# columns cover the types older TDS versions send differently (date, datetime, money, decimal).
$batchSql = "SELECT p.EnglishProductName AS Product, d.CalendarYear, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales, " +
            "AVG(f.UnitPrice) AS AvgPrice, CAST(MIN(f.OrderDate) AS date) AS FirstDay, MAX(f.ShipDate) AS LastShip " +
            "FROM dbo.FactInternetSalesBig f JOIN dbo.DimProduct p ON p.ProductKey = f.ProductKey JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey " +
            "WHERE f.SalesKey <= 16000000 GROUP BY p.EnglishProductName, d.CalendarYear ORDER BY Sales DESC, Product, d.CalendarYear OPTION (MAXDOP 1)"
$paramSql = "SELECT d.CalendarYear, COUNT_BIG(*) AS Lines, SUM(f.SalesAmount) AS Sales FROM dbo.FactInternetSalesBig f " +
            "JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey WHERE f.SalesKey <= 20000000 AND d.CalendarYear >= @fromYear " +
            "GROUP BY d.CalendarYear ORDER BY 1 OPTION (MAXDOP 1)"
$whoSql = "SELECT SUSER_SNAME() AS LoginName, DB_NAME() AS DatabaseName"
$stateSql = "SELECT @@OPTIONS AS Options, @@TEXTSIZE AS TextBytes, @@LANGUAGE AS Lang, @@DATEFIRST AS DateFirst, " +
            "@@LOCK_TIMEOUT AS LockTimeout, @@TRANCOUNT AS TranCount, DB_NAME() AS Db"
$fast = 0.3          # a hit answers in hundredths of a second; SQL Server takes more than -SlowMs

function Tagged([string]$sql, [string]$variant) { "$sql /* probe $variant */" }

# ---- the gateway ------------------------------------------------------------------------------------
Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyZoom.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = "$root/src/DaffyZoom/bin/$Configuration/net10.0/daffyzoom.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffyzoom-drivers-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYZOOM_HOME = "$work\home"
Write-Host "working in $work"

$password = 'probe-pw'
$target = "Server=tcp:$Server;Initial Catalog=$Database;Encrypt=Mandatory;TrustServerCertificate=True"
"$password`n$password`n" | & $exe user add probe --target "$target;User Id=zoom_reports;Password=Zoom!Reports2026" | Out-Null
& $exe user admin probe on | Out-Null
& $exe db set "$Server/$Database" --mode ttl --max-age 4h | Out-Null
Check "the test database has a ttl policy" ($LASTEXITCODE -eq 0)

$log = "$work\daffyzoom.log"
$proxy = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru `
    -ArgumentList "--port $Port --slow-ms $SlowMs --no-repl --cache-dir `"$work\cache`" --log-file `"$log`"" `
    -RedirectStandardOutput "$work\stdout.txt" -RedirectStandardError "$work\stderr.txt"

$admin = "Server=tcp:127.0.0.1,$Port;User Id=probe;Password=$password;Encrypt=True;TrustServerCertificate=True;Application Name=probe-admin"
function Hits() {
    $cn = New-Object System.Data.SqlClient.SqlConnection $admin
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = "EXEC daffyzoom.health"
        $r = $cmd.ExecuteReader(); [void]$r.Read(); $hits = [long]$r['hits']; $r.Close(); return $hits
    }
    finally { $cn.Close() }
}
function LogLines() { if (Test-Path $log) { @(Get-Content $log) } else { @() } }

# ---- in-process probes: System.Data.SqlClient and OLE DB --------------------------------------------
function Step([string]$step, [System.Data.Common.DbConnection]$cn, [string]$sql, $value = $null, [string]$marker = '@fromYear') {
    try {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = if ($null -ne $value) { $sql.Replace('@fromYear', $marker) } else { $sql }
        $cmd.CommandTimeout = 300
        if ($null -ne $value) {
            $p = $cmd.CreateParameter(); $p.ParameterName = '@fromYear'; $p.DbType = [System.Data.DbType]::Int32; $p.Value = $value
            [void]$cmd.Parameters.Add($p)
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $sha = [Security.Cryptography.SHA256]::Create()
        $text = New-Object Text.StringBuilder
        $rows = 0; $first = $null
        $r = $cmd.ExecuteReader()
        do {
            [void]$text.Append(((0..($r.FieldCount - 1) | ForEach-Object { $r.GetName($_) }) -join ',')).Append("`n")
            $values = New-Object object[] $r.FieldCount
            while ($r.Read()) {
                [void]$r.GetValues($values)
                $line = ($values | ForEach-Object { if ($_ -is [DBNull]) { '' } else { [Convert]::ToString($_, [Globalization.CultureInfo]::InvariantCulture) } }) -join '|'
                [void]$text.Append($line).Append("`n")
                if ($null -eq $first) { $first = $line }
                $rows++
            }
        } while ($r.NextResult())
        $r.Close()
        $seconds = $clock.Elapsed.TotalSeconds
        $digest = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text.ToString())) | ForEach-Object { $_.ToString('x2') })
        return [pscustomobject]@{ step = $step; seconds = $seconds; rows = $rows; digest = $digest; text = $first; error = $null }
    }
    catch { return [pscustomobject]@{ step = $step; error = $_.Exception.Message } }
}

function AdoProbe([string]$variant, [scriptblock]$open, [string]$marker) {
    $batch = Tagged $batchSql $variant; $param = Tagged $paramSql $variant
    function Fresh([string]$step, [string]$sql, $value = $null) {
        try { $cn = & $open } catch { return [pscustomobject]@{ step = $step; error = "connect: $($_.Exception.Message)" } }
        try { Step $step $cn $sql $value $marker } finally { $cn.Close() }
    }
    $steps = @()
    $steps += Fresh 'who' $whoSql
    foreach ($i in 1, 2) { $steps += Fresh "batch$i" $batch; Start-Sleep -Milliseconds 500 }
    try {
        $cn = & $open
        try { $steps += Step 'stateBefore' $cn $stateSql; $steps += Step 'batch3' $cn $batch; $steps += Step 'stateAfter' $cn $stateSql }
        finally { $cn.Close() }
    }
    catch { $steps += [pscustomobject]@{ step = 'stateBefore'; error = "connect: $($_.Exception.Message)" } }
    foreach ($i in 1, 2, 3) { $steps += Fresh "param$i" $param 2012; Start-Sleep -Milliseconds 500 }
    $steps += Fresh 'paramOther' $param 2013
    return $steps
}

# ---- external probes: one program, its plan in PROBE_* variables, one JSON line per step -------------
function ExternalProbe([string]$variant, [string]$program, [string[]]$arguments, [hashtable]$vars) {
    $plan = @{
        PROBE_HOST = '127.0.0.1'; PROBE_PORT = "$Port"; PROBE_USER = 'probe'; PROBE_PASSWORD = $password
        PROBE_DATABASE = $Database; PROBE_APP = "probe-$variant"; PROBE_PAUSE = '0.5'
        PROBE_BATCH = (Tagged $batchSql $variant); PROBE_PARAM = (Tagged $paramSql $variant); PROBE_VALUES = '2012,2013'
        PROBE_STATE = $stateSql; PROBE_WHO = $whoSql
    }
    foreach ($k in $vars.Keys) { $plan[$k] = $vars[$k] }
    foreach ($k in $plan.Keys) { Set-Item "Env:$k" $plan[$k] }
    $ErrorActionPreference = 'Continue'          # a driver's warnings on stderr are not failures
    try { $output = @(& $program @arguments 2>&1 | ForEach-Object { "$_" }) }
    finally { foreach ($k in $plan.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } }
    $steps = @($output | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($steps.Count -eq 0) { $steps = @([pscustomobject]@{ step = 'who'; error = "no output: $(($output | Select-Object -Last 5) -join ' / ')" }) }
    return $steps
}

# ---- command-line probes: one process per step, served told by the hit counter -------------------------
function CliProbe([string]$variant, [string]$program, [string[]]$arguments) {
    $batch = Tagged $batchSql $variant
    function One([string]$step, [string]$sql) {
        $before = Hits
        $ErrorActionPreference = 'Continue'
        $env:SQLCMDPASSWORD = $password
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try { $out = @(& $program @arguments -Q $sql 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE }
        finally { Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue }
        $seconds = $clock.Elapsed.TotalSeconds
        $served = (Hits) - $before
        if ($code -ne 0) { return [pscustomobject]@{ step = $step; error = "exit $code`: $(($out | Select-Object -Last 3) -join ' / ')" } }
        $text = ($out -join "`n")
        $sha = [Security.Cryptography.SHA256]::Create()
        $digest = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('x2') })
        $first = (($out | Where-Object { $_.Trim() }) -join ' / ')
        return [pscustomobject]@{ step = $step; seconds = $seconds; rows = $out.Count; digest = $digest; text = $first; served = $served; error = $null }
    }
    $steps = @()
    $steps += One 'who' $whoSql
    foreach ($i in 1, 2, 3) { $steps += One "batch$i" $batch; Start-Sleep -Milliseconds 500 }
    return $steps
}

# ---- judging one driver's run ----------------------------------------------------------------------
# $servable: whether DaffyZoom may answer this driver from the cache at all. Each group of three runs
# (batch, param, rpc, prepexec) must go to SQL Server twice and be served the third time, identical.
# $served: other steps expected served; $relayed: steps expected to go to SQL Server, with param1's answer.
function Judge([string]$variant, [string]$driver, $steps, [long]$hits, [bool]$servable, [string[]]$served, [string[]]$relayed, [int]$logFrom) {
    $by = @{}; foreach ($s in $steps) { $by[$s.step] = $s }
    $errors = @($steps | Where-Object { $_.error })
    $session = (LogLines | Select-Object -Skip $logFrom | Where-Object { $_ -match '"event":"session.open"' -and $_ -notmatch "probe-admin" } | Select-Object -First 1)
    $tds = if ($session -match ', tds ([^,]+), ([^\[]+?)( \[|, MARS|, relayed)') { $Matches[1] } else { '?' }
    $security = if ($session -match ', tds [^,]+, ([^,]+, Tls\d+)') { $Matches[1] } else { '?' }
    $row = [ordered]@{ Variant = $variant; Driver = $driver; Tds = $tds; Encryption = $security; Connects = 'no'; Batch = '-'; Param = '-'; Prepared = '-'; Notes = '' }

    if ($errors.Count -gt 0) {
        $row.Notes = ($errors | ForEach-Object { "$($_.step): $($_.error)" } | Select-Object -First 2) -join '; '
        Check "$variant ran every step" $false $row.Notes
        if (-not $by['who'] -or $by['who'].error) { $script:matrix.Add([pscustomobject]$row); return }
    }
    $row.Connects = 'yes'
    $who = $by['who'].text
    Check "$variant connects through DaffyZoom as the mapped login ($who)" ($who -match 'zoom_reports' -and $who -match $Database) $who

    $isFast = { param($s) $s -and -not $s.error -and ($(if ($null -ne $s.served) { $s.served -gt 0 } else { $s.seconds -lt $fast })) }
    function Times($names) { ($names | ForEach-Object { $s = $by[$_]; if ($s -and -not $s.error) { "{0:N2}" -f $s.seconds } else { 'x' } }) -join ', ' }
    $expectServed = @()

    foreach ($group in 'batch', 'param', 'rpc', 'prepexec') {
        $names = @("${group}1", "${group}2", "${group}3")
        if (-not $by["${group}1"]) { continue }
        $runs = $names | ForEach-Object { $by[$_] }
        if (@($runs | Where-Object { -not $_ -or $_.error }).Count -gt 0) { continue }
        $same = ($runs[0].digest -eq $runs[1].digest) -and ($runs[0].digest -eq $runs[2].digest)
        if ($servable) {
            $expectServed += "${group}3"
            $ok = -not (& $isFast $runs[0]) -and -not (& $isFast $runs[1]) -and (& $isFast $runs[2])
            Check "$variant $group`: two runs at SQL Server's pace, the third served ($(Times $names) s)" $ok
            Check "$variant $group`: the served answer is identical to SQL Server's, as $driver decodes it ($($runs[0].rows) rows)" $same
            $cell = if ($ok -and $same) { 'served' } else { 'FAILED' }
        }
        else {
            $ok = @($runs | Where-Object { & $isFast $_ }).Count -eq 0
            Check "$variant $group`: never served, every run from SQL Server ($(Times $names) s)" $ok
            Check "$variant $group`: identical run to run ($($runs[0].rows) rows)" $same
            $cell = if ($ok -and $same) { 'relayed' } else { 'FAILED' }
        }
        switch ($group) {
            'batch' { $row.Batch = $cell }
            'param' { $row.Param = $cell }
            'prepexec' { $row.Prepared = $cell }
            default { $row.Notes = "sp_executesql without parameters: $cell" }
        }
    }

    if ($by['stateBefore'] -and $by['stateAfter'] -and -not $by['stateBefore'].error -and -not $by['stateAfter'].error) {
        $third = if ($servable) { 'the hit' } else { 'the third run' }
        Check "$variant`: the session's options are the same after $third as before ($($by['stateAfter'].text))" ($by['stateBefore'].digest -eq $by['stateAfter'].digest) "$($by['stateBefore'].text) / $($by['stateAfter'].text)"
    }
    if ($by['paramOther'] -and -not $by['paramOther'].error -and $by['param1'] -and -not $by['param1'].error) {
        Check ("$variant`: another parameter value is not served that answer ({0:N2} s)" -f $by['paramOther'].seconds) `
            (-not (& $isFast $by['paramOther']) -and $by['paramOther'].digest -ne $by['param1'].digest)
    }
    foreach ($name in $served) {
        $expectServed += $name
        Check ("$variant $name is served ({0:N2} s)" -f $by[$name].seconds) (& $isFast $by[$name])
    }
    foreach ($name in $relayed) {
        $step = $by[$name]
        if (-not $step -or $step.error) { Check "$variant $name ran" $false $(if ($step) { $step.error } else { 'no such step' }); continue }
        Check ("$variant $name goes to SQL Server ({0:N2} s), with the same answer" -f $step.seconds) (-not (& $isFast $step) -and $step.digest -eq $by['param1'].digest)
    }
    if (@($steps | Where-Object { $null -ne $_.served }).Count -eq 0) {
        # External programs report timings only: DaffyZoom's counter must agree with them.
        $timedServed = @($steps | Where-Object { $_.step -notin 'who', 'stateBefore', 'stateAfter' -and (& $isFast $_) }).Count
        Check "$variant`: DaffyZoom counted $hits hit(s), as many as the timings show ($timedServed), as expected ($($expectServed.Count))" ($hits -eq $timedServed -and $hits -eq $expectServed.Count)
    }
    if (-not $servable -and $session -match ', (MARS \(relayed only\)|relayed only: [^\[]+)') { $row.Notes = $Matches[1].Trim() }
    $script:matrix.Add([pscustomobject]$row)
}

function Probe([string]$variant, [string]$driver, [scriptblock]$run, [bool]$servable = $true, [string[]]$served = @(), [string[]]$relayed = @()) {
    if (-not (Wanted $variant)) { return }
    Write-Host "--- $variant ($driver)"
    $logFrom = (LogLines).Count
    $before = Hits
    $steps = & $run
    Judge $variant $driver $steps ((Hits) - $before) $servable $served $relayed $logFrom
}

function Skip([string]$variant, [string]$why) {
    if (-not (Wanted $variant)) { return }
    Write-Host "skip  $variant`: $why" -ForegroundColor Yellow
    $script:skipped.Add("$variant ($why)")
}

try {
    Check "DaffyZoom listens on $Port" (Wait-Port $Port)
    [void](Hits)
    $cer = Get-ChildItem $env:DAFFYZOOM_HOME -Recurse -Filter 'daffyzoom.cer' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    Check "the generated certificate's public half is there, for Strict clients ($cer)" ([bool]$cer)

    # ---- tools ---------------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force $ToolsDir | Out-Null
    $python = Join-Path $ToolsDir 'py\Scripts\python.exe'
    if (-not (Test-Path $python) -and (Get-Command python -ErrorAction SilentlyContinue)) {
        Write-Host "installing pyodbc and pymssql into $ToolsDir\py ..."
        & python -m venv (Join-Path $ToolsDir 'py') | Out-Null
        & $python -m pip install --disable-pip-version-check -q pyodbc pymssql | Out-Null
    }
    $tedious = Join-Path $ToolsDir 'node'
    if (-not (Test-Path "$tedious\node_modules\tedious") -and (Get-Command npm -ErrorAction SilentlyContinue)) {
        Write-Host "installing tedious into $tedious ..."
        New-Item -ItemType Directory -Force $tedious | Out-Null
        '{"name":"daffyzoom-probe-tools","private":true}' | Out-File -Encoding ascii "$tedious\package.json"
        Push-Location $tedious; try { & npm install tedious --ignore-scripts --no-audit --no-fund 2>&1 | Out-Null } finally { Pop-Location }
    }
    $java = (Get-Command java -ErrorAction SilentlyContinue).Source
    if (-not $java -and (Test-Path 'C:\Program Files\DBeaver\jre\bin\java.exe')) { $java = 'C:\Program Files\DBeaver\jre\bin\java.exe' }
    if (-not $JdbcJar) {
        $JdbcJar = Get-ChildItem "$env:APPDATA\DBeaverData\drivers", $ToolsDir -Recurse -Filter 'mssql-jdbc-*.jar' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    # Probe.java, compiled: by javac beside java if there is one, else by Eclipse's compiler (ecj),
    # fetched from Maven Central and checked against its published SHA-1 - DBeaver's runtime has none.
    $javaClasses = Join-Path $ToolsDir 'java\classes'
    if ($java -and $JdbcJar -and ((Wanted 'jdbc') -or (Wanted 'jdbc-strict'))) {
        New-Item -ItemType Directory -Force $javaClasses | Out-Null
        Remove-Item "$javaClasses\*.class" -ErrorAction SilentlyContinue
        $javac = Join-Path (Split-Path $java) 'javac.exe'
        $ErrorActionPreference = 'Continue'
        if (Test-Path $javac) { & $javac -nowarn -cp $JdbcJar -d $javaClasses "$drivers\Probe.java" 2>&1 | Out-Host }
        else {
            $ecj = Join-Path $ToolsDir 'java\ecj-3.46.100.jar'
            if (-not (Test-Path $ecj)) {
                Write-Host "fetching Eclipse's Java compiler (ecj 3.46.100) into $ToolsDir\java ..."
                $ecjUrl = 'https://repo1.maven.org/maven2/org/eclipse/jdt/ecj/3.46.100/ecj-3.46.100.jar'
                Invoke-WebRequest -UseBasicParsing $ecjUrl -OutFile $ecj
                $published = (Invoke-WebRequest -UseBasicParsing "$ecjUrl.sha1").Content.Trim()
                if ((Get-FileHash $ecj -Algorithm SHA1).Hash -ne $published) { Remove-Item $ecj; throw "ecj's checksum does not match Maven Central's" }
            }
            & $java -jar $ecj -17 -nowarn -cp $JdbcJar -d $javaClasses "$drivers\Probe.java" 2>&1 | Out-Host
        }
        $ErrorActionPreference = 'Stop'
    }
    $odbcDrivers = @(Get-OdbcDriver -Platform '64-bit' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $odbcSqlcmd = Get-ChildItem 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC' -Recurse -Filter 'SQLCMD.EXE' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    $goSqlcmd = (Get-Command sqlcmd -ErrorAction SilentlyContinue).Source
    if ($goSqlcmd -and -not ((& $goSqlcmd --version 2>&1 | Out-String) -match 'Version: v')) { $goSqlcmd = $null }

    $host_ = "tcp:127.0.0.1,$Port"

    # ---- Microsoft.Data.SqlClient -------------------------------------------------------------------
    $mdsProject = Join-Path $drivers 'mds\MdsProbe.csproj'
    $mdsExe = Join-Path $drivers "mds\bin\Release\net10.0\MdsProbe.exe"
    if (($Only.Count -eq 0) -or ($Only | Where-Object { $_ -like 'mds*' })) {
        dotnet build $mdsProject -c Release -v q -nologo | Out-Null
    }
    $mds = "Server=$host_;User Id=probe;Password=$password;Initial Catalog=$Database;Encrypt=Mandatory;TrustServerCertificate=True"
    # prepexec1..3: SqlCommand.Prepare, run on a connection each (sp_prepexec), the third served; then
    # execute, by the handle that hit carried (sp_execute), which goes to SQL Server and must work.
    Probe 'mds' 'Microsoft.Data.SqlClient 6.1' { ExternalProbe 'mds' $mdsExe @() @{ PROBE_CONNECT = "$mds;Application Name=probe-mds" } } -relayed @('execute')
    Probe 'mds-strict' 'Microsoft.Data.SqlClient 6.1, Encrypt=Strict' {
        ExternalProbe 'mds-strict' $mdsExe @() @{ PROBE_CONNECT = "Server=$host_;User Id=probe;Password=$password;Initial Catalog=$Database;Encrypt=Strict;ServerCertificate=$cer;Application Name=probe-mds-strict" } } -relayed @('execute')
    Probe 'mds-mars' 'Microsoft.Data.SqlClient 6.1, MARS' {
        ExternalProbe 'mds-mars' $mdsExe @() @{ PROBE_CONNECT = "$mds;MultipleActiveResultSets=True;Application Name=probe-mds-mars" } } -servable $false -relayed @('execute')

    # ---- System.Data.SqlClient (.NET Framework) -----------------------------------------------------
    Probe 'sqlclient-netfx' 'System.Data.SqlClient (.NET Framework 4.8)' {
        AdoProbe 'sqlclient-netfx' {
            $cn = New-Object System.Data.SqlClient.SqlConnection "Server=$host_;User Id=probe;Password=$password;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=True;Application Name=probe-sqlclient-netfx"
            $cn.Open(); $cn } '@fromYear' }

    # ---- ODBC ---------------------------------------------------------------------------------------
    function Odbc([string]$variant, [string]$driverName, [string]$options, [bool]$servable = $true) {
        if (-not (Wanted $variant)) { return }
        if ($odbcDrivers -notcontains $driverName) { Skip $variant "'$driverName' is not installed"; return }
        if (-not (Test-Path $python)) { Skip $variant 'no Python for pyodbc'; return }
        $connect = "DRIVER={$driverName};SERVER=$host_;UID=probe;PWD=$password;DATABASE=$Database;APP=probe-$variant;$options"
        # pyodbc prepares every parameterised query (SQLPrepare): param1..3 are sp_prepexec.
        Probe $variant "ODBC: $driverName" { ExternalProbe $variant $python @("$drivers\probe.py") @{ PROBE_KIND = 'odbc'; PROBE_CONNECT = $connect } } -servable $servable
    }
    Odbc 'odbc18' 'ODBC Driver 18 for SQL Server' 'Encrypt=yes;TrustServerCertificate=yes'
    Odbc 'odbc18-strict' 'ODBC Driver 18 for SQL Server' "Encrypt=strict;ServerCertificate=$cer"
    Odbc 'odbc17' 'ODBC Driver 17 for SQL Server' 'Encrypt=yes;TrustServerCertificate=yes'
    # The Windows 'SQL Server' driver knows no TrustServerCertificate: it connects as old applications
    # configure it, asking for no encryption, and DaffyZoom upgrades it to TLS. The same for SQLOLEDB.
    Odbc 'odbc-legacy' 'SQL Server' '' -servable $false

    # ---- OLE DB -------------------------------------------------------------------------------------
    function OleDb([string]$variant, [string]$provider, [string]$options, [bool]$servable = $true) {
        if (-not (Wanted $variant)) { return }
        if (-not (Test-Path "Registry::HKEY_CLASSES_ROOT\$provider")) { Skip $variant "the $provider provider is not installed"; return }
        $connect = "Provider=$provider;Data Source=$host_;Initial Catalog=$Database;User ID=probe;Password=$password;Application Name=probe-$variant;$options"
        Probe $variant "OLE DB: $provider" {
            AdoProbe $variant { $cn = New-Object System.Data.OleDb.OleDbConnection $connect; $cn.Open(); $cn } '?' } -servable $servable
    }
    OleDb 'oledb19' 'MSOLEDBSQL19' 'Use Encryption for Data=Mandatory;Trust Server Certificate=True'
    OleDb 'oledb-legacy' 'SQLOLEDB' '' -servable $false

    # ---- TDS 7.1 logins: the drivers built into Windows, where 0.1.3 went wrong ---------------------
    # Their LOGIN7 has an 86-byte fixed part. 0.1.3 read bytes 86-93 - the host name's first letters -
    # as a change-password entry, and refused the login whenever that landed inside the packet: a
    # long application name does it on any machine. Its refusals were in the TDS 7.2 shape, which
    # these drivers report as "SQL Server version 6.5 and all previous versions are no longer
    # supported". And it lost track of the database after a USE.
    if (Wanted 'legacy-logins') {
        Write-Host "--- legacy-logins (the Windows 'SQL Server' ODBC driver and SQLOLEDB, TDS 7.1)"
        function Legacy([string]$kind, [string]$connect) {
            $cn = if ($kind -eq 'odbc') { New-Object System.Data.Odbc.OdbcConnection $connect } else { New-Object System.Data.OleDb.OleDbConnection $connect }
            if ($kind -eq 'odbc') { $cn.ConnectionTimeout = 5 }
            try { $cn.Open(); $c = $cn.CreateCommand(); $c.CommandText = 'SELECT DB_NAME()'; "ok $($c.ExecuteScalar())" }
            catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; "refused: $(($e.Message -split "`r?`n")[0])" }
            finally { $cn.Close() }
        }
        function LogSince([int]$from, [string]$event) {
            Start-Sleep -Milliseconds 800     # the log file is written on a thread of its own
            @(LogLines | Select-Object -Skip $from | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.event -eq $event })
        }
        $longApp = 'probe-legacy-' + ('x' * 100)
        $odbcLegacy = "Driver={SQL Server};Server=$host_;Uid=probe"
        $oledbLegacy = "Provider=SQLOLEDB;Data Source=$host_;User ID=probe;Connect Timeout=5"

        if ($odbcDrivers -notcontains 'SQL Server') { Skip 'legacy-logins' "the 'SQL Server' ODBC driver is not installed" }
        else {
            $logFrom = (LogLines).Count
            $r = Legacy 'odbc' "$odbcLegacy;Pwd=$password;App=$longApp;Database=$Database;Encrypt=no"
            Check "TDS 7.1, ODBC: a long login logs in, not taken for a password change" ($r -eq "ok $Database") $r
            $r = Legacy 'odbc' "$odbcLegacy;Pwd=not-the-password;App=probe-legacy-refused;Encrypt=no"
            Check "TDS 7.1, ODBC: a wrong password reads as SQL Server's own refusal" ($r -like "*Login failed for user 'probe'*") $r
            $login7 = @(LogSince $logFrom 'session.login7' | Where-Object { $_.msg -like "*tds=7.1*interface='ODBC'*flags=E0-03-*" })
            Check "TDS 7.1, ODBC: the login log line names the interface and option flags" ($login7.Count -ge 1)

            # A USE in a relayed session: DaffyZoom must know the database it left, for daffyzoom.sessions
            # and for the ttl flush of a write that follows it.
            function AdminValue([string]$sql, [scriptblock]$pick) {
                $a = New-Object System.Data.SqlClient.SqlConnection $admin; $a.Open()
                try {
                    $cmd = $a.CreateCommand(); $cmd.CommandText = $sql
                    $rd = $cmd.ExecuteReader(); $value = $null
                    while ($rd.Read()) { $v = & $pick $rd; if ($null -ne $v) { $value = $v } }
                    $rd.Close(); $value
                }
                finally { $a.Close() }
            }
            $cn = New-Object System.Data.Odbc.OdbcConnection "$odbcLegacy;Pwd=$password;App=probe-legacy-use;Encrypt=no"
            $cn.Open()
            try {
                $c = $cn.CreateCommand(); $c.CommandText = 'USE master'; [void]$c.ExecuteNonQuery()
                $db = AdminValue 'EXEC daffyzoom.sessions' { param($rd) if ($rd['application'] -eq 'probe-legacy-use') { [string]$rd['database'] } }
                Check "TDS 7.1, ODBC: DaffyZoom follows a USE (daffyzoom.sessions says '$db')" ($db -eq 'master')

                # Back again, then a write. zoom_reports may only read, so SQL Server refuses it, and
                # WHERE 1 = 0 would change nothing anyway; DaffyZoom flushes the database all the same.
                $c.CommandText = "USE $Database"; [void]$c.ExecuteNonQuery()
                $before = [long](AdminValue 'EXEC daffyzoom.health' { param($rd) $rd['invalidated'] })
                $c.CommandText = 'UPDATE dbo.DimProductCategory SET EnglishProductCategoryName = EnglishProductCategoryName WHERE 1 = 0'
                try { [void]$c.ExecuteNonQuery() } catch { }
                $after = [long](AdminValue 'EXEC daffyzoom.health' { param($rd) $rd['invalidated'] })
                Check "TDS 7.1, ODBC: a write after a USE flushes its ttl database (invalidated $before -> $after)" ($after -gt $before)
            }
            finally { $cn.Close() }

            # Encrypt=yes: the driver checks the certificate, cannot be told to trust a self-signed one,
            # and hangs up after TLS. DaffyZoom says why. A connection that sends nothing is logged too.
            $logFrom = (LogLines).Count
            $r = Legacy 'odbc' "$odbcLegacy;Pwd=$password;App=probe-legacy-tls;Encrypt=yes"
            Check "TDS 7.1, ODBC, Encrypt=yes: the driver turns down the self-signed certificate" ($r -like 'refused:*') $r
            (New-Object Net.Sockets.TcpClient('127.0.0.1', $Port)).Close()
            $early = LogSince $logFrom 'session.closed_early'
            Check "TDS 7.1, ODBC, Encrypt=yes: DaffyZoom logs why the client left" (@($early | Where-Object { $_.msg -like '*after the TLS handshake*certificate*' }).Count -ge 1)
            Check "a connection that sends nothing is logged with a reason" (@($early | Where-Object { $_.msg -like '*without sending PRELOGIN*' }).Count -ge 1)
        }

        if (-not (Test-Path 'Registry::HKEY_CLASSES_ROOT\SQLOLEDB')) { Skip 'legacy-logins' 'the SQLOLEDB provider is not installed' }
        else {
            $r = Legacy 'oledb' "$oledbLegacy;Password=$password;Application Name=$longApp;Initial Catalog=$Database"
            Check "TDS 7.1, SQLOLEDB: a long login logs in, not taken for a password change" ($r -eq "ok $Database") $r
            $r = Legacy 'oledb' "$oledbLegacy;Password=not-the-password;Application Name=probe-legacy-refused"
            Check "TDS 7.1, SQLOLEDB: a wrong password reads as SQL Server's own refusal" ($r -like "*Login failed for user 'probe'*") $r
        }
    }

    # ---- JDBC ---------------------------------------------------------------------------------------
    $jarName = if ($JdbcJar) { [IO.Path]::GetFileNameWithoutExtension($JdbcJar) } else { 'mssql-jdbc' }
    if (-not $java) { Skip 'jdbc' 'no Java (PATH or DBeaver)'; Skip 'jdbc-strict' 'no Java' }
    elseif (-not $JdbcJar) { Skip 'jdbc' 'no mssql-jdbc jar (-JdbcJar, or DBeaver downloads it)'; Skip 'jdbc-strict' 'no mssql-jdbc jar' }
    elseif (-not (Test-Path "$javaClasses\Probe.class")) { Skip 'jdbc' 'Probe.java did not compile'; Skip 'jdbc-strict' 'Probe.java did not compile' }
    else {
        $jdbcArgs = @('-cp', "$javaClasses;$JdbcJar", 'Probe')
        $url = "jdbc:sqlserver://127.0.0.1:$Port;databaseName=$Database"
        # warm1..3 run as sp_executesql, the same request as param1..3: served. prepexec1..3 are
        # sp_prepexec, the third served; execute runs by the handle it carried (sp_execute): to SQL Server.
        Probe 'jdbc' $jarName { ExternalProbe 'jdbc' $java $jdbcArgs @{ PROBE_URL = "$url;encrypt=true;trustServerCertificate=true;applicationName=probe-jdbc" } } `
            -served @('warm1', 'warm2', 'warm3') -relayed @('execute')
        Probe 'jdbc-strict' "$jarName, encrypt=strict" {
            ExternalProbe 'jdbc-strict' $java $jdbcArgs @{ PROBE_URL = "$url;encrypt=strict;serverCertificate=$cer;hostNameInCertificate=localhost;applicationName=probe-jdbc-strict" } } `
            -served @('warm1', 'warm2', 'warm3') -relayed @('execute')
    }

    # ---- tedious (Node) -------------------------------------------------------------------------------
    if (-not (Test-Path "$tedious\node_modules\tedious")) { Skip 'tedious' 'no Node, or tedious could not be installed' }
    else {
        $tediousVersion = (Get-Content "$tedious\node_modules\tedious\package.json" -Raw | ConvertFrom-Json).version
        Probe 'tedious' "tedious $tediousVersion (Node $(& node --version))" { ExternalProbe 'tedious' 'node' @("$drivers\probe.js", $tedious) @{} }
    }

    # ---- pymssql (FreeTDS) ------------------------------------------------------------------------------
    if (-not (Test-Path $python)) { Skip 'pymssql' 'no Python' }
    else {
        $freetds = & $python -c "import pymssql; print('pymssql', pymssql.__version__, pymssql.get_dbversion())" 2>$null
        Probe 'pymssql' "$freetds" { ExternalProbe 'pymssql' $python @("$drivers\probe.py") @{ PROBE_KIND = 'pymssql'; PROBE_TDS = '7.4' } }
    }

    # ---- command lines ----------------------------------------------------------------------------------
    if (-not $odbcSqlcmd) { Skip 'sqlcmd-odbc' 'no ODBC sqlcmd' }
    else {
        Probe 'sqlcmd-odbc' 'sqlcmd (ODBC)' { CliProbe 'sqlcmd-odbc' $odbcSqlcmd @('-S', $host_, '-U', 'probe', '-d', $Database, '-N', '-C', '-W', '-s', '|') }
    }
    if (-not $goSqlcmd) { Skip 'sqlcmd-go' 'no Go sqlcmd' }
    else {
        Probe 'sqlcmd-go' 'sqlcmd (Go, go-mssqldb)' { CliProbe 'sqlcmd-go' $goSqlcmd @('-S', "127.0.0.1,$Port", '-U', 'probe', '-d', $Database, '-N', 'true', '-C', '-W', '-s', '|') }
    }
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}

$errors = @(LogLines | Where-Object { $_ -match '"level":"error"' })
Check "DaffyZoom logged no errors" ($errors.Count -eq 0) ($errors -join "`n")

Write-Host ""
$script:matrix | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
foreach ($s in $script:skipped) { Write-Host "skipped: $s" -ForegroundColor Yellow }

if ($script:failures -gt 0) { Write-Host "$script:failures check(s) failed; DaffyZoom's log is $log" -ForegroundColor Red; exit 1 }
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Remove-Item Env:\DAFFYZOOM_HOME
Write-Host "all checks passed" -ForegroundColor Green
