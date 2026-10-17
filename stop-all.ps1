<#
  Stops the gateway and the 11 services started by run-all.ps1. Kafka keeps running
  (stop it with: docker compose -f deploy\docker-compose.yml down).
#>
$names = 'Gateway', 'Identity', 'Employees', 'Leave', 'Attendance', 'Payroll', 'Expenses', 'Performance', 'Recruitment', 'Helpdesk', 'Chat', 'Notifications'

$processes = Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains ($_.ProcessName -replace '^HrPayroll\.', '') -and $_.ProcessName -like 'HrPayroll.*' }
$processes | ForEach-Object { Write-Host "  stopping $($_.ProcessName)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

# The windows that hosted them
Get-Process powershell -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'HR *' -and $names -contains ($_.MainWindowTitle -replace '^HR ', '') } |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Write-Host "Stopped."
