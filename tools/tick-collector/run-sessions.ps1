param(
    [string]$Config = "$env:LOCALAPPDATA\FtdcNet.CTP\collector.json",
    [int]$MaxLatenessMinutes = 15,
    [switch]$Once,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$mutex = New-Object System.Threading.Mutex($false, "Local\FtdcNet.CTP.SessionScheduler")
if (-not $mutex.WaitOne(0)) {
    Write-Host "another session scheduler is already running"
    exit 0
}

$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repo

if (!(Test-Path $Config)) {
    throw "collector config not found: $Config"
}
$cfg = Get-Content $Config -Raw -Encoding UTF8 | ConvertFrom-Json

function Get-NextSession {
    $now = Get-Date
    $dayStart = [timespan]::Parse($cfg.dayStart)
    $nightStart = [timespan]::Parse($cfg.nightStart)

    for ($offset = 0; $offset -le 7; $offset++) {
        $d = $now.Date.AddDays($offset)
        if ($d.DayOfWeek -eq [DayOfWeek]::Saturday -or
            $d.DayOfWeek -eq [DayOfWeek]::Sunday) {
            continue
        }

        $day = $d.Add($dayStart)
        $night = $d.Add($nightStart)

        if ($offset -eq 0) {
            if ($now -ge $day -and $now -le $day.AddMinutes(10)) {
                return @{ Kind = "day"; Time = $now }
            }
            if ($now -ge $night -and $now -le $night.AddMinutes(10)) {
                return @{ Kind = "night"; Time = $now }
            }
        }

        if ($day -gt $now) { return @{ Kind = "day"; Time = $day } }
        if ($night -gt $now) { return @{ Kind = "night"; Time = $night } }
    }

    throw "no session found in the next 7 days"
}

function Invoke-Session {
    param([string]$Kind)

    $now = Get-Date
    if ($Kind -eq "day") {
        $db = Join-Path "data" ("bars_day_" + $now.ToString("yyyyMMdd") + ".duckdb")
        $minTime = $cfg.dayMinTime
        $duration = [int]$cfg.dayDurationSec
    }
    else {
        $db = Join-Path "data" ("bars_night_" + $now.ToString("yyyyMMdd") + ".duckdb")
        $minTime = $cfg.nightMinTime
        $duration = [int]$cfg.nightDurationSec
    }

    $shards = 1
    if ($cfg.PSObject.Properties.Name -contains "mdShards") {
        $shards = [int]$cfg.mdShards
    }

    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] starting {1} session -> {2}" -f
        (Get-Date), $Kind, $db)

    & (Join-Path $PSScriptRoot "run-night.ps1") `
        -User $cfg.user `
        -Password $cfg.password `
        -Broker $cfg.broker `
        -MdFront $cfg.mdFront `
        -TdFront $cfg.tdFront `
        -AppId $cfg.appId `
        -AuthCode $cfg.authCode `
        -Contracts $cfg.contracts `
        -DurationSec $duration `
        -StartAt (Get-Date).ToString("HH:mm:ss") `
        -Retries 30 `
        -Database $db `
        -MinTime $minTime `
        -MdShards $shards

    return $LASTEXITCODE
}

if ($DryRun) {
    $next = Get-NextSession
    Write-Host ("next session: {0} at {1:yyyy-MM-dd HH:mm:ss}" -f $next.Kind, $next.Time)
    exit 0
}

while ($true) {
    $next = Get-NextSession
    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] next session: {1} at {2:yyyy-MM-dd HH:mm:ss}" -f
        (Get-Date), $next.Kind, $next.Time)

    while ((Get-Date) -lt $next.Time) {
        Start-Sleep -Seconds 30
    }

    $lateness = (Get-Date) - $next.Time
    if ($lateness.TotalMinutes -gt $MaxLatenessMinutes) {
        Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] missed {1} session by {2:F1} min; skipping" -f
            (Get-Date), $next.Kind, $lateness.TotalMinutes)
        continue
    }

    $code = Invoke-Session -Kind $next.Kind
    Write-Host ("[{0:yyyy-MM-dd HH:mm:ss}] {1} session finished, exit={2}" -f
        (Get-Date), $next.Kind, $code)

    if ($Once) { exit $code }
    Start-Sleep -Seconds 60
}
