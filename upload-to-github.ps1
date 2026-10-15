$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git ist nicht installiert. Bitte Git for Windows installieren.' }
if (-not (Test-Path .git)) { git init -b main; if ($LASTEXITCODE -ne 0) { throw 'Git init fehlgeschlagen.' } }
$remote = git remote get-url origin 2>$null
if ($LASTEXITCODE -ne 0) { git remote add origin https://github.com/404Repo/airtray.git }
elseif ($remote -ne 'https://github.com/404Repo/airtray.git') { throw "Unerwartetes origin: $remote" }
git add .
if ($LASTEXITCODE -ne 0) { throw 'Git add fehlgeschlagen.' }
git -c user.name=404Repo -c user.email=404Repo@users.noreply.github.com commit -m 'Initial AirTray 1.0 and Windows installer workflow'
if ($LASTEXITCODE -ne 0) { throw 'Git commit fehlgeschlagen (oder keine Aenderungen vorhanden).' }
git push -u origin main
if ($LASTEXITCODE -ne 0) { throw 'Push fehlgeschlagen. Pruefe Anmeldung und Repository-Berechtigungen.' }
Write-Host 'AirTray erfolgreich hochgeladen: https://github.com/404Repo/airtray' -ForegroundColor Green