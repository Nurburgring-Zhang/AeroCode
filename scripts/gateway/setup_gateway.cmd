@echo off
rem moa-gateway-pro one-click deploy shim -> setup_gateway.ps1
rem Usage: setup_gateway.cmd [install^|start^|stop^|status^|info] [args...]
setlocal
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%setup_gateway.ps1" %*
exit /b %ERRORLEVEL%
