@echo off
cd /d "%~dp0Server"
echo Starting RT Stream Server on port 7777...
echo Press Ctrl+C or close this window to stop.
echo.
dotnet run
pause
