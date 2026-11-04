@echo off
rem Reach the trader program from your phone anywhere, through Tailscale: a private network between your own devices,
rem nothing opened to the internet. Install Tailscale on this computer (https://tailscale.com/download/windows) and
rem sign in first; then double-click this. It prints the address to open on the phone (with Tailscale signed in there).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0share-trader-anywhere.ps1"
pause
