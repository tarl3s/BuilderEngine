# Builds BuilderEngine into bin\test-all (never the owner's bin\Debug, which Windows locks while the editor is open)
# and runs every check, one summary line each, plus the failed lines. Exit code 1 if anything failed.
#   powershell -ExecutionPolicy Bypass -File tools\Test-All.ps1               # build + unit tests + every --snap mode
#   powershell -ExecutionPolicy Bypass -File tools\Test-All.ps1 -Only unit,qol -NoBuild
# Modes: unit, snap (the full editor run on the demo project), qol, prefabs, systems, lifecycle, deepdelve, nightshift, hollow,
# emberduel. A snap mode passes when the app exits by itself, writes no error.txt, every checks file has no FAIL line
# and at least the expected number of PASS lines (a snap that stops early writes fewer lines, not FAIL ones).
param([string[]]$Only = @(), [switch]$NoBuild)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP 'be-test-all'
# Inside the repo's git-ignored bin\ folder, so the systems snap's export finds src\BuilderEngine.Player like a source
# build does (a folder under %TEMP% has no source tree above it).
$out = Join-Path $root 'bin\test-all'
$exe = Join-Path $out 'BuilderEngine.exe'

# name -> sample to build first ('' = an empty folder, i.e. the demo project), extra args, checks files, minimum PASS lines
$modes = [ordered]@{
    snap       = @{ sample = '';               args = @();                        files = @('checks.txt', 'blocks_checks.txt'); min = 169 }
    qol        = @{ sample = '';               args = @('--snap-only', 'qol');       files = @('checks.txt');           min = 32 }
    prefabs    = @{ sample = '';               args = @('--snap-only', 'prefabs');   files = @('prefab_checks.txt');    min = 16 }
    systems    = @{ sample = '';               args = @('--snap-only', 'systems');   files = @('checks.txt');           min = 19 }
    lifecycle  = @{ sample = '';               args = @('--snap-only', 'lifecycle'); files = @('checks.txt');           min = 21 }
    deepdelve  = @{ sample = 'Deepdelve';      args = @('--snap-only', 'deepdelve'); files = @('checks.txt');           min = 6 }
    nightshift = @{ sample = 'Night Shift';    args = @();                        files = @('nightshift_checks.txt'); min = 6 }
    hollow     = @{ sample = 'Hollow Lantern'; args = @('--snap-only', 'hollow');    files = @('checks.txt');           min = 6 }
    emberduel  = @{ sample = 'Ember Duel';     args = @();                        files = @('checks.txt');           min = 6 }
}
$run = if ($Only.Count) { $Only } else { @('unit') + @($modes.Keys) }

if (-not $NoBuild) {
    $build = dotnet build (Join-Path $root 'src\BuilderEngine\BuilderEngine.csproj') "-p:OutDir=$out/" -nologo -v:q 2>&1
    if ($LASTEXITCODE -ne 0) { 'BUILD FAILED'; $build | Select-String ' error ' | Select-Object -First 15 | ForEach-Object { $_.Line.Trim() }; exit 1 }
    $warn = @($build | Select-String ' warning ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique).Count
    "build ok ($warn warning lines)"
}

$failed = 0
foreach ($m in $run) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($m -eq 'unit') {
        $log = Join-Path $work 'unit.log'
        New-Item -ItemType Directory -Force $work | Out-Null
        dotnet run --project (Join-Path $root 'tests\BuilderEngine.Tests') -nologo 2>&1 | Out-File $log -Encoding utf8
        $code = $LASTEXITCODE
        $lines = Get-Content $log -Encoding UTF8
        $summary = ($lines | Where-Object { $_ -match '^\d+ passed, \d+ failed' } | Select-Object -Last 1)
        '{0,-11} {1} ({2}s)' -f 'unit', $(if ($summary) { $summary } else { "(no summary line, exit $code)" }), [int]$sw.Elapsed.TotalSeconds
        if ($code -ne 0 -or -not $summary) {
            $failed++
            $lines | Where-Object { $_ -match '^\s*FAIL|error' } | Select-Object -First 20 | ForEach-Object { '    ' + $_.Trim() }
        }
        continue
    }
    if (-not $modes.Contains($m)) { "{0,-11} unknown mode" -f $m; $failed++; continue }
    $cfg = $modes[$m]
    $base = Join-Path $work $m
    Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
    $proj = Join-Path $base 'proj'; $snap = Join-Path $base 'snap'
    New-Item -ItemType Directory -Force $proj, $snap | Out-Null
    if ($cfg.sample) {
        $p = Start-Process $exe -ArgumentList @('--make-sample', "`"$($cfg.sample)`"", "`"$proj`"") -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0) { "{0,-11} --make-sample failed (exit {1})" -f $m, $p.ExitCode; $failed++; continue }
    }
    $argv = @('--project', "`"$proj`"", '--snap', "`"$snap`"", '--mute') + $cfg.args
    $p = Start-Process $exe -ArgumentList $argv -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(600000)) { $p.Kill(); "{0,-11} TIMEOUT (killed after 10 min)" -f $m; $failed++; continue }
    $pass = 0; $fails = @(); $missing = @()
    foreach ($f in $cfg.files) {
        $path = Join-Path $snap $f
        if (-not (Test-Path $path)) { $missing += $f; continue }
        $l = Get-Content $path -Encoding UTF8
        $pass += @($l | Where-Object { $_ -like 'PASS *' }).Count
        $fails += @($l | Where-Object { $_ -like 'FAIL *' })
    }
    $err = Join-Path $snap 'error.txt'
    $problems = @()
    if (Test-Path $err) { $problems += 'error.txt: ' + ((Get-Content $err -Encoding UTF8 | Select-Object -First 1) -join '') }
    if ($missing.Count) { $problems += 'missing ' + ($missing -join ', ') }
    if ($fails.Count) { $problems += "$($fails.Count) FAIL" }
    if ($pass -lt $cfg.min) { $problems += "only $pass PASS lines (expected at least $($cfg.min))" }
    $status = if ($problems.Count) { 'FAILED: ' + ($problems -join '; ') } else { 'ok' }
    '{0,-11} {1} PASS, {2} FAIL - {3} ({4}s)' -f $m, $pass, $fails.Count, $status, [int]$sw.Elapsed.TotalSeconds
    if ($problems.Count) { $failed++; $fails | Select-Object -First 20 | ForEach-Object { '    ' + $_ } }
}
if ($failed) { "$failed check(s) FAILED (logs and PNGs in $work)"; exit 1 } else { "ALL GREEN (logs and PNGs in $work)" }
