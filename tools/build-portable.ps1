# Builds the portable edition: one self-contained FluenTTY.exe (PORTABLE defined, settings kept in a
# "FluenTTY Data" folder next to the exe). Output: dist\FluenTTY-Portable-<version>.exe
# Like FluxCap Portable, the single file is NOT compressed: compressed self-extracting exes trip EDR heuristics.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $root "FluenTTY\FluenTTY.csproj"
$out = Join-Path $root "FluenTTY\bin\portable"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "Publishing FluenTTY Portable $version..."
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $out -nologo -v q `
    -p:Portable=true -p:PublishReadyToRun=true -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $out "FluenTTY.exe"
$extra = Get-ChildItem $out -File | Where-Object { $_.Name -ne "FluenTTY.exe" -and $_.Extension -notin '.pdb', '.xml' }
if ($extra) { Write-Warning "Files next to the portable exe (should be none): $($extra.Name -join ', ')" }

New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
$target = Join-Path $root "dist\FluenTTY-Portable-$version.exe"
Copy-Item $exe $target -Force
Write-Host "Portable: $target ($([math]::Round((Get-Item $target).Length / 1MB, 1)) MB)"
