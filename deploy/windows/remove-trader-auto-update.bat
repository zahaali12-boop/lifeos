@echo off
rem Stops automatic updates of the trader program: removes the "Quicker Trader auto-update" scheduled task. Nothing else changes.
powershell -NoProfile -Command "Unregister-ScheduledTask -TaskName 'Quicker Trader auto-update' -Confirm:$false" && echo Automatic updates of the trader program are off.
pause
