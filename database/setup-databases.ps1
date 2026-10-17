<#
  Creates the 11 service databases and fills them from the single database.

    powershell -File database\setup-databases.ps1                     # HrPayroll_* from HrPayrollDb on this PC
    powershell -File database\setup-databases.ps1 -Server myserver -SourceDb HrPayrollDb

  - A service database that already exists is not touched (its schema script does nothing).
  - Tables that already have rows are skipped by the data move, so running it twice is safe.
  - The single database (HrPayrollDb) is only read. If it does not exist yet, it is created first from
    ..\..\database\HrPayroll_Database.sql (with its demo data), so a new PC needs only this script.
#>
param(
    [string]$Server = '.',
    [string]$SourceDb = 'HrPayrollDb',
    [string]$Prefix = 'HrPayroll_'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$services = 'Identity', 'Employees', 'Leave', 'Attendance', 'Payroll', 'Expenses', 'Performance', 'Recruitment', 'Helpdesk', 'Chat', 'Notifications'

function Invoke-Sql([string]$File, [string[]]$Vars) {
    $sqlArgs = @('-S', $Server, '-E', '-b', '-I', '-f', '65001', '-i', $File)
    if ($Vars) { $sqlArgs += '-v'; $sqlArgs += $Vars }
    & sqlcmd @sqlArgs
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $File" }
}

$exists = (& sqlcmd -S $Server -E -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$SourceDb') IS NULL THEN 0 ELSE 1 END").Trim()
if ($exists -eq '0') {
    Write-Host "== $SourceDb does not exist - creating it from HrPayroll_Database.sql" -ForegroundColor Cyan
    Invoke-Sql (Join-Path $here '..\..\database\HrPayroll_Database.sql')
}

Write-Host "== Service databases ($Prefix*)" -ForegroundColor Cyan
foreach ($s in $services) {
    Invoke-Sql (Join-Path $here "services\$s.sql") @("DbName=$Prefix$s")
}

Write-Host "== Moving data from $SourceDb" -ForegroundColor Cyan
Invoke-Sql (Join-Path $here 'migrate-from-single-database.sql') @("SourceDb=$SourceDb", "Prefix=$Prefix")
Write-Host "Done." -ForegroundColor Green
