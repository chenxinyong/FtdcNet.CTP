param(
    [Parameter(Mandatory = $true)][string]$User,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$Broker = "9999",
    [Parameter(Mandatory = $true)][string]$MdFront,
    [Parameter(Mandatory = $true)][string]$TdFront,
    [string]$AppId = "",
    [string]$AuthCode = "",
    [int]$DurationSec = 7200,
    [string]$StartAt = "21:00",
    [int]$Retries = 30,
    [string]$Database = "data/bars_live.duckdb",
    [string]$Contracts = "data/contracts.json",
    [string]$MinTime = "",
    [int]$MdShards = 1
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repo

$target = [datetime]::Today.Add([timespan]::Parse($StartAt))
if ((Get-Date) -gt $target) {
    $target = Get-Date
}
Write-Host ("waiting until {0:yyyy-MM-dd HH:mm:ss} ..." -f $target)
while ((Get-Date) -lt $target) {
    Start-Sleep -Seconds 10
}

for ($i = 1; $i -le $Retries; $i++) {
    $runArgs = @(
        "run", "--project", "tools/tick-collector/TickCollector", "--no-build", "--",
        "--broker", $Broker,
        "--user", $User,
        "--password", $Password,
        "--md-front", $MdFront,
        "--td-front", $TdFront,
        "--db", $Database,
        "--contracts", $Contracts,
        "--duration", $DurationSec,
        "--md-shards", $MdShards
    )
    if ($AppId) { $runArgs += @("--appid", $AppId) }
    if ($AuthCode) { $runArgs += @("--authcode", $AuthCode) }
    if ($MinTime) { $runArgs += @("--min-time", $MinTime) }

    Write-Host ("attempt {0} at {1:HH:mm:ss}, db={2}" -f $i, (Get-Date), $Database)
    & dotnet @runArgs
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-Host "tick collector finished OK"
        exit 0
    }
    Write-Host ("attempt {0} failed (exit {1}), retrying in 60s ..." -f $i, $code)
    Start-Sleep -Seconds 60
}

Write-Host "tick collector failed after $Retries attempts"
exit 1
