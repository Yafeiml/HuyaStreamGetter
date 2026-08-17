param(
    [string]$Version = "v1.0.1"
)

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  HuyaStreamGetter Local Release Packaging ($Version)" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$distDir = "$PSScriptRoot/dist"
$winDir = "$distDir/win-x64"

if (Test-Path $winDir) {
    Remove-Item -Recurse -Force $winDir
}
New-Item -ItemType Directory -Force -Path $winDir | Out-Null

Write-Host "`n[1/3] Publishing single-file self-contained win-x64 binary..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot/HuyaStreamGetter.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $winDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[ERROR] dotnet publish failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n[2/3] Copying template configs and documents..." -ForegroundColor Yellow
Copy-Item "$PSScriptRoot/config.example.json" -Destination "$winDir/config.example.json" -Force
Copy-Item "$PSScriptRoot/config.example.json" -Destination "$winDir/config.json" -Force
Copy-Item "$PSScriptRoot/README.md" -Destination "$winDir/README.md" -Force
Copy-Item "$PSScriptRoot/LICENSE" -Destination "$winDir/LICENSE" -Force

Write-Host "`n[3/3] Creating zip archive..." -ForegroundColor Yellow
$zipPath = "$distDir/HuyaStreamGetter-$Version-win-x64.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
Compress-Archive -Path "$winDir/*" -DestinationPath $zipPath -Force

Write-Host "`n===================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Package generated at:" -ForegroundColor Green
Write-Host "  $zipPath" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Green
