# Opens BuilderEngine, always at the latest version of the code in this folder (like EasyEngine's Start-EasyEngine.ps1).
# If any source file is newer than the last build, it rebuilds first (with a small "Updating..." window), then starts
# src\BuilderEngine\bin\Debug\net8.0\BuilderEngine.exe. The Start menu shortcut runs this script:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "<repo>\tools\Start-BuilderEngine.ps1"
param([switch]$Force) # -Force: rebuild even if nothing looks changed

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$proj = Join-Path $src 'BuilderEngine\BuilderEngine.csproj'
$bin = Join-Path $src 'BuilderEngine\bin\Debug\net8.0'
$exe = Join-Path $bin 'BuilderEngine.exe'
$dll = Join-Path $bin 'BuilderEngine.dll'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function Test-Stale {
    if ($Force -or -not (Test-Path $dll)) { return $true }
    $built = (Get-Item $dll).LastWriteTime
    $newer = Get-ChildItem $src -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.bs', '.csproj' -and $_.LastWriteTime -gt $built } |
        Select-Object -First 1
    return $null -ne $newer
}

function Test-Running {
    @(Get-Process BuilderEngine -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0
}

trap {
    "$(Get-Date) $_`n$($_.ScriptStackTrace)" | Out-File (Join-Path $env:TEMP 'builderengine_start.log') -Append
    [System.Windows.Forms.MessageBox]::Show("BuilderEngine couldn't start: $_`n`nLog: $env:TEMP\builderengine_start.log", 'BuilderEngine', 'OK', 'Error') | Out-Null
    exit 1
}

$stale = Test-Stale
if ($stale -and (Test-Running)) {
    # Windows locks the exe while one is open, so it can't be rebuilt: say so instead of quietly opening the old version
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "There is a newer version of BuilderEngine, but it can't update while BuilderEngine is open.`n`n" +
        "Save your work, close every BuilderEngine window and open it again from this shortcut to update.`n`n" +
        "Open another window of the version that is open now?", 'BuilderEngine', 'YesNo', 'Information')
    if ($answer -ne 'Yes') { exit 0 }
}
elseif ($stale) {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'BuilderEngine'; $form.Width = 360; $form.Height = 110; $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'; $form.ControlBox = $false; $form.TopMost = $true
    $form.BackColor = [System.Drawing.Color]::FromArgb(17, 17, 17)
    $label = New-Object System.Windows.Forms.Label
    $label.Text = 'Updating BuilderEngine to the latest version...'; $label.AutoSize = $false; $label.Dock = 'Fill'
    $label.TextAlign = 'MiddleCenter'; $label.ForeColor = [System.Drawing.Color]::FromArgb(217, 119, 87)
    $label.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.Controls.Add($label); $form.Show(); [System.Windows.Forms.Application]::DoEvents()

    $log = Join-Path $env:TEMP 'builderengine_build.log'
    $p = Start-Process dotnet -ArgumentList 'build', "`"$proj`"", '-nologo', '-v:q' `
        -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $log -PassThru
    $null = $p.Handle # PowerShell 5.1 only keeps the exit code if the handle was taken while the process ran
    while (-not $p.WaitForExit(100)) { [System.Windows.Forms.Application]::DoEvents() }
    $form.Close()

    if ($p.ExitCode -ne 0) {
        $errors = (Get-Content $log | Where-Object { $_ -match ' error ' } | Select-Object -First 5) -join "`n"
        [System.Windows.Forms.MessageBox]::Show("The update failed, so the previous version will open.`n`n$errors`n`nFull log: $log", 'BuilderEngine', 'OK', 'Warning') | Out-Null
    }
}

if (Test-Path $exe) { Start-Process $exe -WorkingDirectory $bin }
else { [System.Windows.Forms.MessageBox]::Show("BuilderEngine isn't built yet and the build failed. See $env:TEMP\builderengine_build.log", 'BuilderEngine', 'OK', 'Error') | Out-Null }
