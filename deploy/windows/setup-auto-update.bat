@echo off
rem One-time setup of automatic updates on Windows (double-click it once):
rem   1. installs Git when it is missing (through winget);
rem   2. turns this folder into a Git checkout of the branch below (a GitHub sign-in window opens the first time);
rem   3. registers the "Quicker auto-update" scheduled task, which runs update.bat every 30 minutes and at sign-in,
rem      hidden: it takes what is new, rebuilds, and the database is upgraded by the migrator on start;
rem   4. updates once now.
rem Your .env and your data (in Docker's volumes) are kept. Undo with remove-auto-update.bat.
setlocal EnableExtensions
set "BRANCH=claude/efficient-request-ajd9zy"
set "REPO=https://github.com/zahaali12-boop/lifeos.git"
cd /d "%~dp0..\.."
echo Setting up automatic updates for %CD%
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
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register-task.ps1" || (echo Could not register the scheduled task. & pause & exit /b 1)
echo.

echo Updating now (the first build takes about 10 minutes)...
call "%~dp0update.bat" --force
if errorlevel 1 (
  echo The update did not finish. The details are in update.log.
) else (
  echo Done. From now on updates arrive by themselves; update.log lists each one.
)
pause
