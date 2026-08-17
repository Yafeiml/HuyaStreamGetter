@echo off
chcp 65001 > nul
echo ===================================================
echo   HuyaStreamGetter 本地一键打包 Release 工具
echo ===================================================
echo.

set VER=v1.0.1
if not exist "dist" mkdir "dist"

echo [1/3] 正在发布 Windows x64 免安装独立单文件...
dotnet publish HuyaStreamGetter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "./dist/win-x64"

if %ERRORLEVEL% NEQ 0 (
    echo [错误] 编译失败！
    pause
    exit /b %ERRORLEVEL%
)

echo [2/3] 正在拷贝配置文件模板与说明文档...
copy /Y "config.example.json" "dist\win-x64\config.example.json" > nul
copy /Y "config.example.json" "dist\win-x64\config.json" > nul
copy /Y "README.md" "dist\win-x64\README.md" > nul
copy /Y "LICENSE" "dist\win-x64\LICENSE" > nul

echo [3/3] 正在压缩打包为 zip 文件...
powershell -Command "Compress-Archive -Path ./dist/win-x64/* -DestinationPath ./dist/HuyaStreamGetter-%VER%-win-x64.zip -Force"

echo.
echo ===================================================
echo  [成功] 打包完成！
echo  产物位置: dist/HuyaStreamGetter-%VER%-win-x64.zip
echo ===================================================
echo.
pause
