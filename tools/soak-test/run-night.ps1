param(
    [Parameter(Mandatory = $true)][string]$User,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$Broker = "9999",
    [Parameter(Mandatory = $true)][string]$MdFront,
    [Parameter(Mandatory = $true)][string]$TdFront,
    [string]$AppId = "",
    [string]$AuthCode = "",
    [string]$Instruments = "cu2612,SA709,TA709,SA705",
    [int]$DurationSec = 6900,
    [int]$QueryInterval = 10,
    [int]$OrderInterval = 20,
    [switch]$PlaceOrder,
    [string]$StartAt = "21:00",
    [int]$Retries = 30,
    [string]$OutputRoot = "tools/soak-test/results"
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
    $out = Join-Path $OutputRoot ("night-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-a$i")
    $runArgs = @(
        "run", "--project", "tools/soak-test/SoakTest", "--no-build", "--",
        "--broker", $Broker,
        "--user", $User,
        "--password", $Password,
        "--md-front", $MdFront,
        "--td-front", $TdFront,
        "--instruments", $Instruments,
        "--duration", $DurationSec,
        "--query-interval", $QueryInterval,
        "--order-interval", $OrderInterval,
        "--output", $out
    )
    if ($AppId) { $runArgs += @("--appid", $AppId) }
    if ($AuthCode) { $runArgs += @("--authcode", $AuthCode) }
    if ($PlaceOrder) { $runArgs += "--place-order" }

    Write-Host ("attempt {0} at {1:HH:mm:ss} -> {2}" -f $i, (Get-Date), $out)
    & dotnet @runArgs
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-Host "soak finished OK: $out"
        exit 0
    }
    Write-Host ("attempt {0} failed (exit {1}), retrying in 60s ..." -f $i, $code)
    Start-Sleep -Seconds 60
}

Write-Host "soak failed after $Retries attempts"
exit 1
