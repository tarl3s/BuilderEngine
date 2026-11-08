<#
.SYNOPSIS
  Builds the UI-free BuilderEngine library EasyEngine links against (BuilderEngine.Script + BuilderEngine.Plugin) in
  Release and copies the DLLs to a folder (default: the sibling EasyEngine repo's lib\BuilderEngine\).
.EXAMPLE
  tools\Build-LinkLib.ps1
  tools\Build-LinkLib.ps1 -Target C:\src\engine\lib\BuilderEngine
#>
param(
    [string]$Target = (Join-Path $PSScriptRoot '..\..\engine\lib\BuilderEngine')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'src\BuilderEngine.Script\BuilderEngine.Script.csproj'
$out = Join-Path ([IO.Path]::GetTempPath()) ('be-linklib-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

dotnet build $proj -c Release -nologo -v q -clp:ErrorsOnly -p:OutDir="$out\"
if ($LASTEXITCODE -ne 0) { throw "BuilderEngine.Script did not build (exit $LASTEXITCODE)." }

New-Item -ItemType Directory -Force $Target | Out-Null
foreach ($name in 'BuilderEngine.Script.dll', 'BuilderEngine.Plugin.dll', 'BuilderEngine.Plugin.xml') {
    $src = Join-Path $out $name
    if (Test-Path $src) { Copy-Item $src (Join-Path $Target $name) -Force }
    elseif ($name -like '*.dll') { throw "$name was not built." }
}
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$commit = (git -C $repo rev-parse --short HEAD 2>$null)
Write-Host "BuilderEngine link library ($commit) copied to $((Resolve-Path $Target).Path)"
