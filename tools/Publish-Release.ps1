# Publishes the current version (from BuilderEngine.csproj) as a GitHub release that people download and the engine's
# updater installs: a self-contained build (no .NET needed) zipped as BuilderEngine-win-x64.zip, with builderengine-files.txt
# listing every file (the updater removes exactly those when the next version comes).
# The release also has player/ (the exported-games player, see GameExport.cs).
# The tag vX.Y.Z must already be pushed.   powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -NotesFile notes.md
param([string]$NotesFile = '', [string]$Version = '', [switch]$NoUpload)
if (-not $NoUpload -and -not $NotesFile) { throw "-NotesFile is needed to publish (or pass -NoUpload to only build the zip)" }
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

# The player of exported games (Project > Export game... copies player\ next to the game; people who download
# BuilderEngine have no .NET SDK to build it). Self-contained too, so exported games need nothing installed.
dotnet publish (Join-Path $root 'src/BuilderEngine.Player/BuilderEngine.Player.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'player') -p:DebugType=None -p:Version=$Version -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the player failed" }
if (-not (Test-Path (Join-Path $out 'player\BuilderEngine.Player.exe'))) { throw "the player wasn't built" }

$manifest = 'builderengine-files.txt'
$files = Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Substring($out.Length + 1) }
($files + $manifest) | Set-Content (Join-Path $out $manifest) -Encoding ASCII
# Not Compress-Archive (nor ZipFile.CreateFromDirectory): in Windows PowerShell 5.1 both write entries as "player\x.dll".
# Backslashes are not valid zip paths (other unzip tools make files with "\" in their names), so each entry is named here.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stream = [System.IO.File]::Create($zip)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($rel in ($files + $manifest)) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $out $rel), $rel.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose(); $stream.Dispose() }
"built $zip ($([Math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, $($files.Count + 1) files)"

if (-not $NoUpload) {
    & $gh release create "v$Version" $zip --repo tarl3s/BuilderEngine --title "BuilderEngine v$Version" --notes-file $NotesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}
