@echo off
REM Kill all running Wsync instances
taskkill /IM Wsync.exe /F 2>nul
if errorlevel 1 (
    echo No Wsync instances are running.
) else (
    echo All Wsync instances have been terminated.
)
