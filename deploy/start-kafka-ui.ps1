<#
  Kafka UI without Docker (the same UI docker-compose.yml runs): topics, messages, consumer groups and lag.

      powershell -File deploy\start-kafka-ui.ps1        # then open http://localhost:8085

  Needs Java 17+ and kafka-ui-api-v0.7.2.jar (https://github.com/provectus/kafka-ui/releases) in %LOCALAPPDATA%\HrKafka\kafka-ui.jar.
#>
param(
    [string]$Jar = (Join-Path $env:LOCALAPPDATA 'HrKafka\kafka-ui.jar'),
    [string]$Java = (Join-Path $env:LOCALAPPDATA 'HrPayrollMobileTools\jdk\bin\java.exe'),
    [string]$BootstrapServers = 'localhost:9092',
    [int]$Port = 8085
)
if (-not (Test-Path $Java)) { $Java = (Get-Command java -ErrorAction Stop).Source }
if (-not (Test-Path $Jar)) {
    Write-Host "Downloading Kafka UI..."
    Invoke-WebRequest -Uri 'https://github.com/provectus/kafka-ui/releases/download/v0.7.2/kafka-ui-api-v0.7.2.jar' -OutFile $Jar -UseBasicParsing
}
$cmd = "`$Host.UI.RawUI.WindowTitle = 'HR Kafka UI'; " +
       "`$env:KAFKA_CLUSTERS_0_NAME = 'hr-payroll'; `$env:KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS = '$BootstrapServers'; `$env:SERVER_PORT = '$Port'; " +
       "& '$Java' -Xmx512m -jar '$Jar'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $cmd -WindowStyle Minimized | Out-Null
Write-Host "Kafka UI is starting - open http://localhost:$Port in about 20 seconds."
