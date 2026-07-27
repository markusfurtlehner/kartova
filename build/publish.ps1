<#
.SYNOPSIS
  Publishes DirStat as a self-contained application for one or more platforms.

.DESCRIPTION
  Each build embeds the .NET runtime, so the result runs on a machine with nothing
  installed. Windows and Linux produce a single executable file; macOS produces a
  .app bundle, because that is the only form Finder will launch.

.EXAMPLE
  ./publish.ps1                       # host platform only
  ./publish.ps1 -Runtime all          # every supported platform
  ./publish.ps1 -Runtime linux-x64
#>
[CmdletBinding()]
param(
  [string]$Runtime = 'host',
  [string]$Configuration = 'Release',
  [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'

$AllRuntimes = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')

function Get-HostRuntime {
  if ($IsWindows -or $env:OS -eq 'Windows_NT') { $os = 'win' }
  elseif ($IsMacOS) { $os = 'osx' }
  else { $os = 'linux' }

  $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower()
  if ($arch -ne 'arm64') { $arch = 'x64' }
  return "$os-$arch"
}

function New-AppBundle {
  param([string]$Rid, [string]$PublishDir, [string]$Version, [string]$Repo)

  # Finder only launches a bundle, so the flat publish output is rehomed into one.
  $bundle = Join-Path (Split-Path $PublishDir -Parent) "$Rid-bundle/DirStat.app"
  $macos = Join-Path $bundle 'Contents/MacOS'
  $resources = Join-Path $bundle 'Contents/Resources'

  New-Item -ItemType Directory -Force -Path $macos, $resources | Out-Null
  Copy-Item (Join-Path $PublishDir '*') $macos -Recurse -Force

  $icon = Join-Path $Repo 'src/DirStat.App/Assets/dirstat.png'
  if (Test-Path $icon) { Copy-Item $icon (Join-Path $resources 'dirstat.png') -Force }

  $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DirStat</string>
  <key>CFBundleDisplayName</key><string>DirStat</string>
  <key>CFBundleIdentifier</key><string>org.dirstat.app</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>DirStat</string>
  <key>CFBundleIconFile</key><string>dirstat</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSRequiresAquaSystemAppearance</key><false/>
</dict>
</plist>
"@

  Set-Content -Path (Join-Path $bundle 'Contents/Info.plist') -Value $plist -Encoding utf8
  Write-Host '    bundled DirStat.app'
}

# ------------------------------------------------------------------------ main

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/DirStat.App/DirStat.App.csproj'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }

$targets = switch ($Runtime) {
  'all'   { $AllRuntimes }
  'host'  { @(Get-HostRuntime) }
  default { @($Runtime) }
}

$props = Join-Path $repo 'Directory.Build.props'
$version = (Select-String -Path $props -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value

Write-Host "DirStat $version" -ForegroundColor Cyan
Write-Host "Publishing: $($targets -join ', ')`n"

foreach ($rid in $targets) {
  $outDir = Join-Path $OutputRoot $rid
  Write-Host "==> $rid" -ForegroundColor Yellow

  if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

  # PublishSelfContained switches on the single-file, trimmed profile in the csproj.
  dotnet publish $project -c $Configuration -r $rid -o $outDir -p:PublishSelfContained=true --nologo -v quiet

  if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

  if ($rid.StartsWith('osx')) {
    New-AppBundle -Rid $rid -PublishDir $outDir -Version $version -Repo $repo
  }

  $size = (Get-ChildItem $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
  Write-Host ("    {0,-14} {1,7:N1} MB" -f $rid, $size) -ForegroundColor Green
}

Write-Host "`nArtifacts in $OutputRoot" -ForegroundColor Cyan
