# Gracefully close all running Wsync instances
$wsyncProcesses = Get-Process -Name "Wsync" -ErrorAction SilentlyContinue

if ($wsyncProcesses.Count -eq 0) {
    Write-Host "No Wsync instances are running."
    exit 0
}

if ($wsyncProcesses.Count -eq 1) {
    Write-Host "Closing 1 Wsync instance gracefully..."
} else {
    Write-Host "Closing $($wsyncProcesses.Count) Wsync instances gracefully..."
}

# Try graceful shutdown first using CloseMainWindow
foreach ($process in $wsyncProcesses) {
    $process.CloseMainWindow() | Out-Null
}

# Wait up to 5 seconds for processes to close gracefully
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ($stopwatch.Elapsed.TotalSeconds -lt 5) {
    $stillRunning = Get-Process -Name "Wsync" -ErrorAction SilentlyContinue
    if ($stillRunning.Count -eq 0) {
        Write-Host "All Wsync instances closed gracefully."
        exit 0
    }
    Start-Sleep -Milliseconds 100
}

# If any processes are still running after 5 seconds, force kill them
$stillRunning = Get-Process -Name "Wsync" -ErrorAction SilentlyContinue
if ($stillRunning.Count -gt 0) {
    Write-Host "Force killing $($stillRunning.Count) instance(s) that did not close gracefully..."
    $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "All Wsync instances have been terminated."
} else {
    Write-Host "All Wsync instances closed gracefully."
}
