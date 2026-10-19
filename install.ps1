# Watchblox one-line installer.
# Friends install by pasting this into PowerShell (no GitHub browsing, no unzip):
#   irm https://raw.githubusercontent.com/omggSarahhwastaken/Watchblox/main/install.ps1 | iex
# The script downloads the latest release installer and runs it. The
# installer self-elevates (UAC prompt) when it needs to write to Program Files.
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/omggSarahhwastaken/Watchblox/releases/latest/download/WatchbloxInstaller_x64.exe'
$tmp = Join-Path $env:TEMP 'WatchbloxInstaller_x64.exe'
Write-Host 'Downloading Watchblox…'
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
Write-Host 'Running installer…'
Start-Process -FilePath $tmp -Wait
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host 'Done — Watchblox is installed.'
