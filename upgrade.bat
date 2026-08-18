@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

title LiveStreamGateway - Docker Compose 一键升级工具

echo ===================================================
echo   LiveStreamGateway Docker Compose 一键升级工具
echo ===================================================
echo.

cd /d "%~dp0"

echo [1/4] 正在检查 Docker 运行环境...
docker --version > nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [错误] 未检测到 Docker 环境，请确保 Docker Desktop 已启动。
    echo.
    pause
    exit /b 1
)

echo [2/4] 正在拉取最新的 LiveStreamGateway 镜像...
docker compose pull
if %ERRORLEVEL% NEQ 0 (
    echo [提示] 尝试兼容旧版 docker-compose 命令拉取...
    docker-compose pull
    if %ERRORLEVEL% NEQ 0 (
        echo [错误] 拉取新镜像失败，请检查网络连接或 Docker 状态。
        echo.
        pause
        exit /b 1
    )
)

echo.
echo [3/4] 正在平滑重启并应用新容器...
docker compose up -d --remove-orphans
if %ERRORLEVEL% NEQ 0 (
    docker-compose up -d --remove-orphans
)

echo.
echo [4/4] 正在清理旧版本的虚悬残留镜像...
docker image prune -f > nul 2>&1

echo.
echo ===================================================
echo   🎉 LiveStreamGateway 已成功升级到最新版本！
echo   管理看板地址: http://localhost:9898
echo   M3U 订阅源:   http://localhost:9898/jellyfin.m3u
echo ===================================================
echo.
pause
