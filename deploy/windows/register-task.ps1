# Registers the "Quicker auto-update" scheduled task for the signed-in user: update.bat at sign-in and every 30
# minutes, with no window (conhost --headless), one run at a time, at most two hours each.
$ErrorActionPreference = 'Stop'
$update = Join-Path $PSScriptRoot 'update.bat'
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless cmd.exe /c `"$update`""
$triggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"),
    (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 30))
)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'Quicker auto-update' -Action $action -Trigger $triggers -Settings $settings `
    -Description 'Checks GitHub for a new version of Quicker every 30 minutes and installs it (deploy\windows\update.bat).' -Force | Out-Null
Write-Host 'Scheduled task "Quicker auto-update" registered: every 30 minutes and at sign-in.'
