param(
    [string]$Version = "v1.0.1"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "  HuyaStreamGetter Release Packaging ($Version)"      -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$root    = $PSScriptRoot
$distDir = "$root/dist"
$outDir  = "$distDir/win-x64"

# ── Clean ──
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ── Step 1: dotnet publish ──
Write-Host "`n[1/4] Publishing self-contained single-file win-x64 binary..." -ForegroundColor Yellow
dotnet publish "$root/HuyaStreamGetter.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) { Write-Host "[ERROR] dotnet publish failed!" -ForegroundColor Red; exit 1 }

# ── Step 2: Copy docs & config ──
Write-Host "`n[2/4] Copying docs and config template..." -ForegroundColor Yellow
Copy-Item "$root/config.example.json" "$outDir/config.example.json" -Force
Copy-Item "$root/config.example.json" "$outDir/config.json"         -Force
Copy-Item "$root/README.md"           "$outDir/README.md"           -Force
Copy-Item "$root/LICENSE"             "$outDir/LICENSE"              -Force

# ── Step 3: Include ffmpeg.exe ──
Write-Host "`n[3/4] Locating FFmpeg..." -ForegroundColor Yellow

$ffmpegSrc = $null

# 3a. Check project root
$localFfmpeg = "$root/ffmpeg.exe"
if (Test-Path $localFfmpeg) {
    $ffmpegSrc = $localFfmpeg
    Write-Host "  Found in project root: $localFfmpeg" -ForegroundColor Green
}

# 3b. Check system PATH
if (-not $ffmpegSrc) {
    $found = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($found) {
        $ffmpegSrc = $found.Source
        Write-Host "  Found in system PATH: $ffmpegSrc" -ForegroundColor Green
    }
}

# 3c. Check dist/ffmpeg folder (user can pre-place here)
$preplacedDir = "$root/dist/ffmpeg"
if (-not $ffmpegSrc -and (Test-Path "$preplacedDir/ffmpeg.exe")) {
    $ffmpegSrc = "$preplacedDir/ffmpeg.exe"
    Write-Host "  Found in dist/ffmpeg/: $ffmpegSrc" -ForegroundColor Green
}

if ($ffmpegSrc) {
    Copy-Item $ffmpegSrc "$outDir/ffmpeg.exe" -Force
    $sizeMB = [math]::Round((Get-Item "$outDir/ffmpeg.exe").Length / 1MB, 1)
    Write-Host "  Included ffmpeg.exe ($sizeMB MB) in package." -ForegroundColor Green
} else {
    Write-Host "  [WARNING] FFmpeg not found. Package will NOT include ffmpeg.exe." -ForegroundColor DarkYellow
    Write-Host "  Users will need to install FFmpeg separately." -ForegroundColor DarkYellow
}

# ── Step 4: Create zip ──
Write-Host "`n[4/4] Creating zip archive..." -ForegroundColor Yellow
$zipPath = "$distDir/HuyaStreamGetter-$Version-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path "$outDir/*" -DestinationPath $zipPath -Force

$zipSizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "====================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Package created!"                         -ForegroundColor Green
Write-Host "  File: $zipPath"                                     -ForegroundColor Green
Write-Host "  Size: $zipSizeMB MB"                                -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Write-Host ""
