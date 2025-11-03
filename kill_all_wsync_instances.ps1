# Kill all running Wsync instances
$wsyncProcesses = Get-Process -Name "Wsync" -ErrorAction SilentlyContinue

if ($wsyncProcesses.Count -eq 0) {
    Write-Host "No Wsync instances are running."
    exit 0
}

if ($wsyncProcesses.Count -eq 1) {
    Write-Host "Killing 1 Wsync instance..."
} else {
    Write-Host "Killing $($wsyncProcesses.Count) Wsync instances..."
}

$wsyncProcesses | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "All Wsync instances have been terminated."
