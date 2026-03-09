# Kill all running Wsync instances
Write-Host "Killing all running Wsync instances..." -ForegroundColor Yellow
$killScript = Join-Path (Split-Path -Parent $PSScriptRoot) "program" | Join-Path -ChildPath "kill_all_wsync_instances.ps1"
if (Test-Path $killScript) {
    & $killScript
    Start-Sleep -Seconds 2  # Give it time to die
} else {
    # Alternative: kill via process
    Get-Process Wsync -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Build the application
Write-Host "`nBuilding Wsync..." -ForegroundColor Yellow
Push-Location (Split-Path -Parent $PSScriptRoot)
dotnet build source
Pop-Location

Write-Host "`nBuild complete!" -ForegroundColor Green
