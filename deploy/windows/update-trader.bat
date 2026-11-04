@echo off
rem Brings the trader program (its own stack, docker-compose.trader.yml) up to date: fetches the branch the folder
rem follows and, when something new arrived, takes it and rebuilds that stack alone; the full system next to it is not
rem touched. Run by the "Quicker Trader auto-update" scheduled task every 30 minutes; double-click it to update at once.
rem "update-trader.bat --force" rebuilds even when nothing new arrived (and waits for another build to finish first).
rem A stack that has never been built is built on the next run either way. Everything is written to update-trader.log.
setlocal EnableExtensions
cd /d "%~dp0..\.."
set "LOG=%CD%\update-trader.log"
set "LOCK=%CD%\update.lock"
set "COMPOSE=docker compose -f docker-compose.trader.yml"
set "FORCE=0"
if /i "%~1"=="--force" set "FORCE=1"

:waitlock
if exist "%LOCK%" (
  rem Another update (of either stack) is still building; a lock older than two hours is from one that was interrupted.
  powershell -NoProfile -Command "if (((Get-Date) - (Get-Item '%LOCK%').LastWriteTime).TotalHours -lt 2) { exit 1 }" >nul 2>&1
  if errorlevel 1 (
    if "%FORCE%"=="0" exit /b 0
    powershell -NoProfile -Command "Start-Sleep -Seconds 30" >nul 2>&1
    goto waitlock
  )
)

where git >nul 2>&1 || (echo [%date% %time%] git is not installed >> "%LOG%" & exit /b 1)
docker info >nul 2>&1 || (echo [%date% %time%] Docker is not running; will try again later >> "%LOG%" & exit /b 0)

git fetch --quiet origin >> "%LOG%" 2>&1 || (echo [%date% %time%] could not reach GitHub; will try again later >> "%LOG%" & exit /b 0)
for /f %%i in ('git rev-parse HEAD') do set "LOCAL=%%i"
for /f %%i in ('git rev-parse "@{u}"') do set "REMOTE=%%i"
rem The stack's API container exists once the stack was built; until then every run builds it.
set "BUILT="
for /f %%i in ('%COMPOSE% ps -a -q api 2^>nul') do set "BUILT=%%i"
if "%LOCAL%"=="%REMOTE%" if "%FORCE%"=="0" if defined BUILT exit /b 0

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
