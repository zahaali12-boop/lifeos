@echo off
rem One-time setup of the trader program on its own (double-click it once): the same steps as setup-auto-update.bat,
rem for the separate stack in docker-compose.trader.yml. It has its own database, files and ports (the app on 5174,
rem its API on 8090), starts with no data at all, and never gets the demo workspace; the full system, if it is
rem installed in this folder too, keeps running untouched. Updates arrive through the "Quicker Trader auto-update"
rem scheduled task (update-trader.bat); undo with remove-trader-auto-update.bat.
setlocal EnableExtensions
set "BRANCH=claude/efficient-request-ajd9zy"
set "REPO=https://github.com/zahaali12-boop/lifeos.git"
cd /d "%~dp0..\.."
echo Setting up the trader program in %CD%
echo.

where git >nul 2>&1
if errorlevel 1 (
  echo Installing Git...
  winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
  set "PATH=%PATH%;%ProgramFiles%\Git\cmd"
)
where git >nul 2>&1 || (echo Git could not be installed. Install it from https://git-scm.com and run this again. & pause & exit /b 1)

if not exist ".git" (
  git init -q
  git remote add origin %REPO%
)
git config core.autocrlf false
echo Connecting to GitHub (sign in if a window asks)...
git fetch origin %BRANCH% || (echo Could not reach the repository. Check the sign-in and the internet connection. & pause & exit /b 1)
git checkout -q -f -B %BRANCH% --track origin/%BRANCH% || (echo Could not check out %BRANCH%. & pause & exit /b 1)
git reset -q --hard origin/%BRANCH%
echo Now at:
git log -1 --format="  %%h %%s"
echo.

echo Registering the scheduled task...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register-task.ps1" -Name "Quicker Trader auto-update" -Script "%~dp0update-trader.bat" || (echo Could not register the scheduled task. & pause & exit /b 1)
echo.

echo Building and starting the trader program (the first build takes about 10 minutes)...
call "%~dp0update-trader.bat" --force
if errorlevel 1 (
  echo The build did not finish. The details are in update-trader.log.
) else (
  echo Done. Open http://localhost:5174 and press "Create workspace": the workspace opens as the trader program.
  echo From now on updates arrive by themselves; update-trader.log lists each one.
)
pause
