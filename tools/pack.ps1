# Builds a release and packs it with Velopack.
#
#   .\tools\pack.ps1 -Version 1.1.0
#
# What a merchant gets is Setup.exe, once. Everything after that arrives on its
# own: the till checks hourly, downloads in the background, and applies at its
# next start. It is never restarted while it is running — see UpdateService.
#
# There is no signing step yet. Windows SmartScreen will warn on an unsigned
# installer, which is a conversation to have with a merchant rather than a
# technical failure; see docs/DEPLOYMENT.md.

param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Channel = "win",
  [string]$OutputDir = "releases"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# The tests are part of packing, not a thing to remember before it. A build that
# ships without them is the one that needed them.
Write-Host "`n== tests ==" -ForegroundColor Cyan
dotnet test RingOrder.Epos.sln --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed; nothing packed" }

Write-Host "`n== publish ==" -ForegroundColor Cyan
$publish = Join-Path $root "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish src/RingOrder.Epos/RingOrder.Epos.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "`n== pack ==" -ForegroundColor Cyan
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
  throw "vpk is not installed. Run: dotnet tool install -g vpk"
}

vpk pack `
  --packId RingOrder.Epos `
  --packTitle "RingOrder EPOS" `
  --packVersion $Version `
  --packDir $publish `
  --channel $Channel `
  --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "`nPacked $Version into $OutputDir." -ForegroundColor Green
Write-Host ""
Write-Host "Publish these as a GitHub release on the RELEASES repository" -ForegroundColor Yellow
Write-Host "  https://github.com/marsnewbie/epos-releases    (public, holds no source)"
Write-Host ""
Write-Host "  RingOrder.Epos-win-Setup.exe   what a new shop downloads, once"
Write-Host "  RingOrder.Epos-*-full.nupkg    what an installed till fetches"
Write-Host "  RingOrder.Epos-*-delta.nupkg   the same, but only what changed (from the second release on)"
Write-Host "  RELEASES / releases.win.json   the manifests the till reads first"
Write-Host ""
Write-Host "Tag the release with the bare version. Do NOT tick pre-release: a shop is"
Write-Host "not a test channel and the till ignores those."
