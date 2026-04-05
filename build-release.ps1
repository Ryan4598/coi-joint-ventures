<#
.SYNOPSIS
    Builds the Joint Ventures launcher as a single self-contained EXE
    with the plugin embedded. BepInEx is downloaded at runtime.

.EXAMPLE
    .\build-release.ps1
#>

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$LauncherDir = Join-Path $RepoRoot "src\Launcher"
$BepInExCacheDir = Join-Path $RepoRoot "bepinex-bundle"

Write-Host "=== Building Joint Ventures ===" -ForegroundColor Cyan
Write-Host ""

# ── Ensure BepInEx DLLs exist for compilation ──
$coreDir = Join-Path $BepInExCacheDir "core"
if (-not (Test-Path (Join-Path $coreDir "BepInEx.dll"))) {
    Write-Host "Downloading BepInEx for compilation..." -ForegroundColor Yellow

    # Fetch latest 5.x release info
    $headers = @{ "User-Agent" = "JointVentures-Build" }
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/BepInEx/BepInEx/releases" -Headers $headers
    $release = $releases | Where-Object { $_.tag_name -match "^v5\." -and -not $_.prerelease } | Select-Object -First 1
    if (-not $release) { throw "Could not find a BepInEx 5.x release" }

    $asset = $release.assets | Where-Object { $_.name -match "^BepInEx_win_x64_.*\.zip$" } | Select-Object -First 1
    if (-not $asset) { throw "Could not find win_x64 asset in release $($release.tag_name)" }

    Write-Host "  Found $($release.tag_name): $($asset.name)"

    $zipPath = Join-Path $RepoRoot "dist\.bepinex-download.zip"
    New-Item -ItemType Directory -Path (Split-Path $zipPath) -Force | Out-Null
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

    # Extract core DLLs and doorstop
    $tempExtract = Join-Path $RepoRoot "dist\.bepinex-extract"
    if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $tempExtract

    New-Item -ItemType Directory -Path $coreDir -Force | Out-Null
    Copy-Item "$tempExtract\BepInEx\core\*.dll" -Destination $coreDir

    $doorstopDir = Join-Path $BepInExCacheDir "doorstop"
    New-Item -ItemType Directory -Path $doorstopDir -Force | Out-Null
    Copy-Item "$tempExtract\winhttp.dll" -Destination $doorstopDir

    # Cleanup
    Remove-Item $zipPath -Force
    Remove-Item $tempExtract -Recurse -Force

    Write-Host "  BepInEx $($release.tag_name) ready for compilation"
    Write-Host ""
}

# ── Build the plugin ──
Write-Host "Building COIJointVentures plugin..." -ForegroundColor Yellow
$pluginProj = Join-Path $RepoRoot "src\COIJointVentures\COIJointVentures.csproj"
dotnet build $pluginProj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

$pluginBinDir = Join-Path $RepoRoot "src\COIJointVentures\bin\$Configuration\net472"
Write-Host ""

# ── Create the plugin-only bundle ZIP ──
Write-Host "Creating plugin bundle ZIP..." -ForegroundColor Yellow

$staging = Join-Path $RepoRoot "dist\.staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item "$pluginBinDir\COIJointVentures.dll" -Destination $staging
if (Test-Path "$pluginBinDir\COIJointVentures.pdb") {
    Copy-Item "$pluginBinDir\COIJointVentures.pdb" -Destination $staging
}

$zipPath = Join-Path $LauncherDir "plugin-bundle.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "  Created $zipPath"

Remove-Item $staging -Recurse -Force
Write-Host ""

# ── Publish the launcher (single-file EXE with embedded plugin) ──
Write-Host "Publishing launcher..." -ForegroundColor Yellow
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

$launcherProj = Join-Path $LauncherDir "Launcher.csproj"
dotnet publish $launcherProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed" }

# Remove PDB from output
Get-ChildItem $OutputDir -Filter "*.pdb" | Remove-Item -Force

# Clean up the zip (embedded in EXE now)
Remove-Item $zipPath -Force
Write-Host ""

# ── Summary ──
$exe = Get-Item (Join-Path $OutputDir "JointVentures.exe")
$sizeMB = "{0:N1} MB" -f ($exe.Length / 1MB)

Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "  $($exe.FullName)  ($sizeMB)" -ForegroundColor White
Write-Host ""
Write-Host "That single EXE is all you need to distribute." -ForegroundColor Yellow
Write-Host "BepInEx will be downloaded automatically on first launch." -ForegroundColor Yellow
