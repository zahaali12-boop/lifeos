@echo off
rem Stops automatic updates: removes the "Quicker auto-update" scheduled task. Nothing else changes.
powershell -NoProfile -Command "Unregister-ScheduledTask -TaskName 'Quicker auto-update' -Confirm:$false" && echo Automatic updates are off.
pause
