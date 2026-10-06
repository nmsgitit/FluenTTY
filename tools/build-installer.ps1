# Publishes FluenTTY (self-contained, win-x64, ReadyToRun) and builds dist\FluenTTY-Setup-<version>.exe with Inno Setup.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $root "FluenTTY\FluenTTY.csproj"
$publish = Join-Path $root "FluenTTY\bin\publish"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "Publishing FluenTTY $version ($Configuration, win-x64, self-contained)..."
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $publish -nologo -v q `
    -p:PublishReadyToRun=true -p:DebugType=none -p:GenerateDocumentationFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Trim files the app never loads (localized satellite resources, XML docs, PDBs)
Get-ChildItem $publish -Directory | Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' } | Remove-Item -Recurse -Force
Get-ChildItem $publish -Include *.pdb,*.xml -Recurse | Remove-Item -Force

$iscc = Get-ChildItem "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install with: winget install JRSoftware.InnoSetup" }

Write-Host "Compiling installer..."
& $iscc.FullName /Q "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $root "installer\FluenTTY.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-ChildItem (Join-Path $root "dist") -Filter "FluenTTY-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Installer: $($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) MB)"
