@echo off
rem Brings the trader program (its own stack, docker-compose.trader.yml) up to date: fetches the branch the folder
rem follows and, when something new arrived, takes it and rebuilds that stack alone; the full system next to it is not
rem touched. Run by the "Quicker Trader auto-update" scheduled task every 30 minutes; double-click it to update at once.
rem "update-trader.bat --force" rebuilds even when nothing new arrived. Everything it does is written to update-trader.log.
setlocal EnableExtensions
cd /d "%~dp0..\.."
set "LOG=%CD%\update-trader.log"
set "LOCK=%CD%\update.lock"
set "COMPOSE=docker compose -f docker-compose.trader.yml"

if exist "%LOCK%" (
  rem Another update (of either stack) is still building; a lock older than two hours is from one that was interrupted.
  powershell -NoProfile -Command "if (((Get-Date) - (Get-Item '%LOCK%').LastWriteTime).TotalHours -lt 2) { exit 1 }" >nul 2>&1
  if errorlevel 1 exit /b 0
)

where git >nul 2>&1 || (echo [%date% %time%] git is not installed >> "%LOG%" & exit /b 1)
docker info >nul 2>&1 || (echo [%date% %time%] Docker is not running; will try again later >> "%LOG%" & exit /b 0)

git fetch --quiet origin >> "%LOG%" 2>&1 || (echo [%date% %time%] could not reach GitHub; will try again later >> "%LOG%" & exit /b 0)
for /f %%i in ('git rev-parse HEAD') do set "LOCAL=%%i"
for /f %%i in ('git rev-parse "@{u}"') do set "REMOTE=%%i"
if "%LOCAL%"=="%REMOTE%" if /i not "%~1"=="--force" exit /b 0

echo.>> "%LOG%"
echo [%date% %time%] updating the trader program %LOCAL:~0,7% to %REMOTE:~0,7% >> "%LOG%"
echo %date% %time% > "%LOCK%"
git reset --hard "@{u}" >> "%LOG%" 2>&1
git log -1 --format="  now at %%h %%s" >> "%LOG%" 2>&1
%COMPOSE% up -d --build >> "%LOG%" 2>&1
if errorlevel 1 (
  echo [%date% %time%] the build failed; the previous version keeps running. See the lines above. >> "%LOG%"
  del "%LOCK%" >nul 2>&1
  exit /b 1
)
echo [%date% %time%] the trader program is updated and running >> "%LOG%"
del "%LOCK%" >nul 2>&1
exit /b 0
