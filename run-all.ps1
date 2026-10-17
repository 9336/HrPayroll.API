<#
  Starts the HR Payroll microservices on this PC: Kafka (Docker), the 11 services and the API gateway.

      powershell -ExecutionPolicy Bypass -File run-all.ps1            # build + start everything
      powershell -ExecutionPolicy Bypass -File run-all.ps1 -NoBuild   # start without building
      powershell -ExecutionPolicy Bypass -File stop-all.ps1           # stop everything (Kafka keeps running)

  Each service runs in its own minimized window ("HR <name>") and logs to logs\<name>.log.
  The web and mobile apps use the gateway exactly like the old single API: https://localhost:7169 / http://localhost:5032.
#>
param(
    [switch]$NoBuild,
    [string]$KafkaHost = 'localhost',
    [int]$KafkaPort = 9092
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null

$services = [ordered]@{
    Identity = 5101; Employees = 5102; Leave = 5103; Attendance = 5104; Payroll = 5105; Expenses = 5106
    Performance = 5107; Recruitment = 5108; Helpdesk = 5109; Chat = 5110; Notifications = 5111
}

function Test-Port([string]$HostName, [int]$Port) {
    $client = New-Object Net.Sockets.TcpClient
    try { return $client.ConnectAsync($HostName, $Port).Wait(700) -and $client.Connected } catch { return $false } finally { $client.Dispose() }
}

# ------------------------------------------------------------------ Kafka
function Wait-Kafka([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Port $KafkaHost $KafkaPort)) {
        if ((Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Seconds 2
    }
    return $true
}

if (-not (Test-Port $KafkaHost $KafkaPort)) {
    $started = $false
    $dockerUp = (Get-Command docker -ErrorAction SilentlyContinue) -and ((docker info --format '{{.ServerVersion}}' 2>$null) -ne $null)
    if ($dockerUp) {
        Write-Host "Starting Kafka (Docker)..." -ForegroundColor Cyan
        docker compose -f (Join-Path $root 'deploy\docker-compose.yml') up -d
        $started = Wait-Kafka 120
    }
    elseif (Test-Path (Join-Path $env:LOCALAPPDATA 'HrKafka\kafka\libs')) {
        Write-Host "Docker is not running - starting the local Kafka (deploy\start-local-kafka.ps1)..." -ForegroundColor Cyan
        & (Join-Path $root 'deploy\start-local-kafka.ps1')
        $started = Wait-Kafka 90
    }
    if (-not $started) {
        Write-Warning ("Kafka is not running on ${KafkaHost}:$KafkaPort (start Docker Desktop, or run deploy\start-local-kafka.ps1). " +
            "The services start anyway and keep retrying; events wait in each service's outbox until Kafka is available.")
    }
}
else { Write-Host "Kafka is running on ${KafkaHost}:$KafkaPort." -ForegroundColor Green }

# ------------------------------------------------------------------ The old single API must not hold the gateway's ports
if (Get-Process -Name 'HrPayroll.Gateway' -ErrorAction SilentlyContinue) {
    Write-Host "The gateway is already running (https://localhost:7169). Run stop-all.ps1 first to restart everything." -ForegroundColor Green
    return
}
foreach ($port in 7169, 5032) {
    if (Test-Port 'localhost' $port) {
        throw "Port $port is already in use - probably the old single API (window 'HrPayroll API'). Close it, then run this again."
    }
}

# ------------------------------------------------------------------ Build
if (-not $NoBuild) {
    Write-Host "Building..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'HrPayroll.Microservices.sln') -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# ------------------------------------------------------------------ Start
function Start-Svc([string]$Name, [string]$Project, [string]$LaunchProfile) {
    $dir = Join-Path $root $Project
    $log = Join-Path $logs "$Name.log"
    $cmd = "`$Host.UI.RawUI.WindowTitle = 'HR $Name'; Set-Location '$dir'; dotnet run --no-build --launch-profile $LaunchProfile 2>&1 | Tee-Object -FilePath '$log'"
    Start-Process powershell -ArgumentList '-NoExit', '-Command', $cmd -WindowStyle Minimized | Out-Null
}

foreach ($name in $services.Keys) {
    if (Test-Port 'localhost' $services[$name]) { Write-Host "  $name already running"; continue }
    Start-Svc $name "src\Services\HrPayroll.$name" 'http'
    Write-Host "  started $name (http://localhost:$($services[$name]))"
}
Start-Svc 'Gateway' 'src\Gateway\HrPayroll.Gateway' 'https'
Write-Host "  started Gateway (https://localhost:7169, http://localhost:5032)"

# ------------------------------------------------------------------ Wait until everything answers
Write-Host "Waiting for the services..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(150)
do {
    Start-Sleep -Seconds 3
    try { $status = Invoke-RestMethod -Uri 'http://localhost:5032/health/services' -TimeoutSec 5; $ok = $true }
    catch { $ok = $false; $status = $null }
} until ($ok -or (Get-Date) -gt $deadline)

if ($ok) {
    Write-Host "All services are up:" -ForegroundColor Green
    $status.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-14} {1}" -f $_.Name, $_.Value) }
    Write-Host "Gateway: https://localhost:7169  (Swagger of each service: http://localhost:51xx/swagger)"
}
else {
    Write-Warning "Not every service answered within 150 seconds - see the windows 'HR <name>' or logs\<name>.log."
}
