# Builds an AppDash release into .\releases: Setup.exe, the full package, and a delta package
# from the previous release in that folder (Velopack makes the delta automatically).
#
#   .\build-release.ps1 -Version 1.0.1
#   .\build-release.ps1 -Version 1.0.1 -Feed https://github.com/<you>/<repo>/releases/latest/download
#
# -Feed is where installed copies look for updates: a folder, file share, or URL that serves the
# contents of .\releases. Upload the new files there after each build (or use `vpk upload`).
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Feed = (Join-Path $PSScriptRoot 'releases')
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'releases'
$pub = Join-Path $PSScriptRoot "publish\$Version"

dotnet publish "$PSScriptRoot\AppDash.csproj" -c Release -r win-x64 --self-contained false -o $pub `
    -p:Version=$Version -p:UpdateFeed=$Feed
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

vpk pack --packId AppDashApp --packTitle AppDash --packVersion $Version --packDir $pub --mainExe AppDash.exe `
    --icon "$PSScriptRoot\app.ico" --framework net8.0-x64-desktop --outputDir $out
if ($LASTEXITCODE) { throw "vpk pack failed ($LASTEXITCODE)" }

Get-ChildItem $out | Sort-Object LastWriteTime | ForEach-Object { '{0,-45} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
