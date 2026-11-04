@echo off
rem Reach the trader program from any phone, anywhere, with nothing installed on the phone: the same Tailscale address
rem opened to the internet (Tailscale Funnel). The program's own sign-in is then what protects it: a long password and
rem two-step verification. Install Tailscale on this computer and sign in first; then double-click this.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0share-trader-anywhere.ps1" -Public
pause
