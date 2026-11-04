@echo off
rem Brings a Windows install up to date: fetches the branch it follows, and when something new arrived, takes it
rem and rebuilds the containers (the migrator runs before the API starts, so the database is upgraded too).
rem Run by the "Quicker auto-update" scheduled task every 30 minutes; double-click it to update at once.
rem "update.bat --force" rebuilds even when nothing new arrived. Everything it does is written to update.log.
setlocal EnableExtensions
cd /d "%~dp0..\.."
set "LOG=%CD%\update.log"
set "LOCK=%CD%\update.lock"

if exist "%LOCK%" (
  rem Another update is still building; a lock older than two hours is from an update that was interrupted.
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
echo [%date% %time%] updating %LOCAL:~0,7% to %REMOTE:~0,7% >> "%LOG%"
echo %date% %time% > "%LOCK%"
git reset --hard "@{u}" >> "%LOG%" 2>&1
git log -1 --format="  now at %%h %%s" >> "%LOG%" 2>&1
docker compose up -d --build >> "%LOG%" 2>&1
if errorlevel 1 (
  echo [%date% %time%] the build failed; the previous version keeps running. See the lines above. >> "%LOG%"
  del "%LOCK%" >nul 2>&1
  exit /b 1
)
echo [%date% %time%] updated and running >> "%LOG%"
del "%LOCK%" >nul 2>&1
exit /b 0
