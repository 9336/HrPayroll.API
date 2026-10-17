<#
  Runs Kafka without Docker (single broker, KRaft mode) - a fallback for PCs where Docker Desktop is not running.

      powershell -File deploy\start-local-kafka.ps1 [-KafkaHome <folder>] [-Java <java.exe>]

  Needs Java 17+ and the Apache Kafka binaries (https://kafka.apache.org/downloads, "Binary downloads", unpacked).
  Data is kept in <KafkaHome>\..\data. The broker listens on localhost:9092 like the Docker one.
#>
param(
    [string]$KafkaHome = (Join-Path $env:LOCALAPPDATA 'HrKafka\kafka'),
    [string]$Java = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Java) {
    $candidates = @((Join-Path $env:LOCALAPPDATA 'HrPayrollMobileTools\jdk\bin\java.exe'))
    if ($env:JAVA_HOME) { $candidates = @(Join-Path $env:JAVA_HOME 'bin\java.exe') + $candidates }
    $Java = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Java) { $Java = (Get-Command java -ErrorAction SilentlyContinue).Source }
}
if (-not $Java) { throw 'Java 17+ was not found. Install a JDK or pass -Java <path to java.exe>.' }
if (-not (Test-Path (Join-Path $KafkaHome 'libs'))) { throw "Kafka binaries not found in $KafkaHome. Pass -KafkaHome <unpacked kafka folder>." }

$base = Split-Path -Parent $KafkaHome
$data = Join-Path $base 'data'
$props = Join-Path $base 'server.properties'
# Java's "libs\*" classpath wildcard avoids the Windows command-line length limit of kafka-run-class.bat.
$classpath = Join-Path $KafkaHome 'libs\*'

if (-not (Test-Path $props)) {
    $lines = (Get-Content (Join-Path $KafkaHome 'config\server.properties')) -replace '^log.dirs=.*', ('log.dirs=' + ($data -replace '\\', '/'))
    $lines += 'auto.create.topics.enable=false'   # topics are created by the services
    Set-Content $props $lines -Encoding ascii
}
if (-not (Test-Path (Join-Path $data 'meta.properties'))) {
    $clusterId = (& $Java -cp $classpath kafka.tools.StorageTool random-uuid 2>$null | Select-Object -Last 1).Trim()
    & $Java -cp $classpath "-Dlog4j2.configurationFile=$KafkaHome\config\tools-log4j2.yaml" kafka.tools.StorageTool format --standalone -t $clusterId -c $props | Out-Null
    Write-Host "Formatted Kafka storage in $data (cluster $clusterId)"
}

$log = Join-Path $base 'kafka-run.log'
$cmd = "`$Host.UI.RawUI.WindowTitle = 'HR Kafka'; & '$Java' -Xms256m -Xmx768m -cp '$classpath' '-Dlog4j2.configurationFile=$KafkaHome\config\log4j2.yaml' '-Dkafka.logs.dir=$base\logs' kafka.Kafka '$props' 2>&1 | Tee-Object -FilePath '$log'"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $cmd -WindowStyle Minimized | Out-Null
Write-Host "Kafka is starting (window 'HR Kafka', log $log)."
