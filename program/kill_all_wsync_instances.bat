@echo off
REM Gracefully close all running Wsync instances
taskkill /IM Wsync.exe 2>nul
if errorlevel 1 (
    echo No Wsync instances are running.
) else (
    echo Wsync instances have been closed.
)

