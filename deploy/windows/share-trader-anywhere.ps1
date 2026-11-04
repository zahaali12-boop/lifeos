# Shares the trader program with the founder's own phone anywhere, through Tailscale: a private network between the
# devices signed in to one account, so nothing is opened to the internet (ADR-0024, amended 2026-11-03; A-203).
# It turns on Tailscale Serve for the app's port, which gives the app an HTTPS address of this machine, writes that
# address as the program's public origin (sign-in with a passkey or security key, and the links in its messages, use
# it) and restarts the stack to take it. Run by share-trader-anywhere.bat; install Tailscale and sign in first.
# With -Public (share-trader-public.bat) the same address is opened to the whole internet through Tailscale Funnel,
# so a phone needs nothing installed: the program's own sign-in is then what protects it (A-204).
param([switch]$Public)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envFile = Join-Path $root '.env'

$port = 5174
if (Test-Path $envFile) {
    $portLine = Get-Content $envFile | Where-Object { $_ -match '^\s*QUICKER_TRADER_WEB_PORT\s*=\s*(\d+)' } | Select-Object -Last 1
    if ($portLine -and $portLine -match '=\s*(\d+)') { $port = [int]$Matches[1] }
}

$tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path $tailscale)) {
    $found = Get-Command tailscale -ErrorAction SilentlyContinue
    $tailscale = if ($found) { $found.Source } else { $null }
}
if (-not $tailscale) {
    Write-Host 'Tailscale is not installed. Install it from https://tailscale.com/download/windows, sign in, then run this again.'
    exit 1
}

$status = & $tailscale status --json | ConvertFrom-Json
if ($status.BackendState -ne 'Running') {
    Write-Host "Tailscale is installed but not signed in (state: $($status.BackendState)). Open Tailscale from the tray, sign in, then run this again."
    exit 1
}
$name = "$($status.Self.DNSName)".TrimEnd('.')
if (-not $name) {
    Write-Host 'Tailscale has no name for this machine yet: turn on MagicDNS at https://login.tailscale.com/admin/dns and run this again.'
    exit 1
}

$how = if ($Public) { 'funnel' } else { 'serve' }
Write-Host "Sharing http://127.0.0.1:$port as https://$name ($(if ($Public) { 'the whole internet, protected by the sign-in' } else { 'your own devices only' })) ..."
& $tailscale $how --bg "http://127.0.0.1:$port"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Tailscale could not share it. If it asked for HTTPS, enable "HTTPS certificates" at https://login.tailscale.com/admin/dns; if it asked for Funnel, open the link it printed and allow it; then run this again.'
    exit 1
}

# The program's public origin: the one line in .env, replaced when it is already there.
$origin = "https://$name"
$lines = @()
if (Test-Path $envFile) {
    $lines = @(Get-Content $envFile) | Where-Object { $_ -notmatch '^\s*QUICKER_TRADER_PUBLIC_ORIGIN\s*=' }
}
$lines += "QUICKER_TRADER_PUBLIC_ORIGIN=$origin"
[System.IO.File]::WriteAllLines($envFile, [string[]]$lines)

Write-Host 'Applying the address to the program (a minute or two)...'
& cmd.exe /c "`"$PSScriptRoot\update-trader.bat`" --force"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'The program did not restart cleanly; see update-trader.log. The address is kept and applies on the next update.'
    exit 1
}

Write-Host ''
if ($Public) {
    Write-Host "Done. On any phone, open"
    Write-Host "  $origin"
    Write-Host 'in Safari and add it to the home screen: nothing to install. Anyone with the address reaches the sign-in page,'
    Write-Host 'so keep the password long and turn on two-step verification (Account, Security). This computer must stay on.'
} else {
    Write-Host "Done. On the phone: install Tailscale from the App Store, sign in with the same account, switch it on, then open"
    Write-Host "  $origin"
    Write-Host 'in Safari and add it to the home screen. It works wherever the phone has internet, as long as this computer is on.'
}
