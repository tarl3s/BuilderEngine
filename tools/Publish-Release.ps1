# Publishes the current version (from BuilderEngine.csproj) as a GitHub release that people download and the engine's
# updater installs: a self-contained build (no .NET needed) zipped as BuilderEngine-win-x64.zip, with builderengine-files.txt
# listing every file (the updater removes exactly those when the next version comes).
# The tag vX.Y.Z must already be pushed.   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NotesFile notes.md
param([Parameter(Mandatory)][string]$NotesFile, [string]$Version = '', [switch]$NoUpload)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'src/BuilderEngine/BuilderEngine.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$work = Join-Path $env:TEMP 'BuilderEngine_publish'
$out = Join-Path $work 'BuilderEngine'
$zip = Join-Path $work 'BuilderEngine-win-x64.zip'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }

dotnet publish (Join-Path $root 'src/BuilderEngine/BuilderEngine.csproj') -c Release -r win-x64 --self-contained true -o $out -p:DebugType=None -p:Version=$Version -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$manifest = 'builderengine-files.txt'
$files = Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Substring($out.Length + 1) }
($files + $manifest) | Set-Content (Join-Path $out $manifest) -Encoding ASCII
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
"built $zip ($([Math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, $($files.Count + 1) files)"

if (-not $NoUpload) {
    & $gh release create "v$Version" $zip --repo tarl3s/BuilderEngine --title "BuilderEngine v$Version" --notes-file $NotesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}
