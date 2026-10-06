# Builds the installer and the portable exe for the version in FluenTTY.csproj and publishes them as a
# GitHub release (tag v<version>), with that version's CHANGELOG.md section as the release notes and a
# SHA256SUMS.txt that the in-app updater verifies downloads against (Services\Updater.cs).
# Needs: git, gh (signed in with `gh auth login`), the code pushed to the repository.
#   powershell -File tools\publish-release.ps1            # build + publish
#   powershell -File tools\publish-release.ps1 -NoBuild   # publish what's already in dist\
param([switch]$NoBuild, [string]$Repo = "")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $root "FluenTTY\FluenTTY.csproj"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"

if (-not $Repo) {
    # The updater only ever looks at this repository; publishing anywhere else would be pointless.
    $Repo = (Select-String -Path (Join-Path $root "FluenTTY\Services\Updater.cs") -Pattern 'Repository = "([^"]+)"').Matches[0].Groups[1].Value
}
if ($Repo -like "OWNER/*") { throw "Set Updater.Repository to the real GitHub repository first." }

# Release notes = this version's section of CHANGELOG.md.
$lines = Get-Content (Join-Path $root "CHANGELOG.md") -Encoding UTF8
$start = ($lines | Select-String -Pattern "^## $([regex]::Escape($version))\b").LineNumber
if (-not $start) { throw "CHANGELOG.md has no '## $version' section. Add one before releasing." }
$section = @()
foreach ($l in $lines[$start..($lines.Count - 1)]) { if ($l -match '^## ') { break }; $section += $l }
$notes = (($section -join "`n").Trim()) + @"


---
**Download:** ``FluenTTY-Setup-$version.exe`` installs FluenTTY (choose just for you or for everyone on the PC). ``FluenTTY-Portable-$version.exe`` runs without installing and keeps its settings in a ``FluenTTY Data`` folder next to it.
Already using FluenTTY 1.2.0 or later? It offers this update itself; your settings and saved sessions are kept.
"@

cmd /c "gh release view $tag --repo $Repo >nul 2>&1"   # (PowerShell 5.1 turns native stderr into errors)
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists. Bump <Version> in FluenTTY.csproj (and CHANGELOG.md) for a new release." }

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build-installer.ps1")
    & (Join-Path $PSScriptRoot "build-portable.ps1")
}
$dist = Join-Path $root "dist"
$files = @("FluenTTY-Setup-$version.exe", "FluenTTY-Portable-$version.exe") | ForEach-Object { Join-Path $dist $_ }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Missing $f" } }

$sums = Join-Path $dist "SHA256SUMS.txt"
($files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) }) -join "`n" |
    Set-Content -Path $sums -Encoding ascii -NoNewline

$notesFile = Join-Path ([IO.Path]::GetTempPath()) "fluentty-release-notes.md"
[IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding $false))

Write-Host "Publishing FluenTTY $version to $Repo..."
gh release create $tag @files $sums --repo $Repo --title "FluenTTY $version" --notes-file $notesFile --latest
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Remove-Item $notesFile -ErrorAction SilentlyContinue
Write-Host "Released: https://github.com/$Repo/releases/tag/$tag"
