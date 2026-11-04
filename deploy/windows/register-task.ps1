# Registers a hidden scheduled task for the signed-in user that runs an update script at sign-in and every 30 minutes
# (conhost --headless, one run at a time, at most two hours each). Without parameters it is the full system's
# "Quicker auto-update" running update.bat; setup-trader.bat passes the trader program's name and script.
param(
    [string]$Name = 'Quicker auto-update',
    [string]$Script = (Join-Path $PSScriptRoot 'update.bat')
)
$ErrorActionPreference = 'Stop'
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless cmd.exe /c `"$Script`""
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"),
    (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 30))
)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $Name -Action $action -Trigger $triggers -Settings $settings `
    -Description "Checks GitHub for a new version of Quicker every 30 minutes and installs it ($Script)." -Force | Out-Null
Write-Host "Scheduled task `"$Name`" registered: every 30 minutes and at sign-in."
