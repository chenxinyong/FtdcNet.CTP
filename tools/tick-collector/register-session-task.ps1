param(
    [string]$TaskName = "FtdcNet.CTP-SessionScheduler"
)

$ErrorActionPreference = "Stop"
$script = Join-Path $PSScriptRoot "run-sessions.ps1"

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description "FtdcNet.CTP daily futures session collector scheduler" `
    -Force | Out-Null

Write-Host "registered scheduled task: $TaskName (starts at logon)"
Write-Host "scheduler script: $script"
