# Packages EZPZ Server Tools.
#   .\mod\package.ps1             builds dist\EZPZServerTools-<version>.zip: the mod, install.cmd and a README
#   .\mod\package.ps1 -Workshop   also stages %USERPROFILE%\Zomboid\Workshop\EZPZServerTools for the game's
#                                 Workshop uploader (main menu > Workshop). Nothing is uploaded by this script.
param([switch]$Workshop)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$mod = Join-Path $PSScriptRoot "EZPZServerTools"
$info = Get-Content (Join-Path $mod "42\mod.info")
$version = (($info | Where-Object { $_ -like "modversion=*" }) -replace "^modversion=", "").Trim()
if (-not $version) { throw "No modversion in mod.info" }

# ---- the zip for sending to people
$dist = Join-Path $root "dist"
$name = "EZPZServerTools-$version"
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse $mod (Join-Path $stage "EZPZServerTools")

$install = @'
@echo off
setlocal
rem Installs EZPZ Server Tools into this PC's Project Zomboid mods folder.
rem For a server's data folder instead:  install.cmd "C:\path\to\server data folder"
set "SRC=%~dp0EZPZServerTools"
set "DEST=%USERPROFILE%\Zomboid\mods"
if not "%~1"=="" set "DEST=%~1\mods"
if not exist "%SRC%\42\mod.info" (
  echo Can't find the EZPZServerTools folder next to this file. Unzip everything first, then run it again.
  pause
  exit /b 1
)
if not exist "%DEST%" mkdir "%DEST%"
if exist "%DEST%\EZPZServerTools" rmdir /s /q "%DEST%\EZPZServerTools"
xcopy /e /i /q "%SRC%" "%DEST%\EZPZServerTools" >nul
if errorlevel 1 (
  echo Copying failed. Is Project Zomboid running? Close it and try again.
  pause
  exit /b 1
)
echo Installed EZPZ Server Tools __VERSION__ to:
echo   %DEST%\EZPZServerTools
echo.
echo You can join servers that use it now.
pause
'@ -replace "__VERSION__", $version
Set-Content -Path (Join-Path $stage "install.cmd") -Value ($install -replace "`r?`n", "`r`n") -Encoding ASCII -NoNewline

$readme = @'
EZPZ Server Tools __VERSION__
==========================

A Project Zomboid (Build 42) mod for servers run with ezpz (https://github.com/kruchone/ezpz).
It shows players the restart and wipe countdowns and the season ladder, and gives the server's
admins live player health on the ezpz dashboard. It adds no items and changes nothing in the game.

PLAYERS
  Double-click install.cmd, then join the server as usual.
  (It copies the mod into %USERPROFILE%\Zomboid\mods. Until the mod is on the Steam Workshop,
  everyone who joins needs this same version installed, or the game won't let them in.)

SERVER (running ezpz)
  1. In ezpz, open the Setup page and click "Open data folder".
  2. Copy the EZPZServerTools folder into the "mods" folder there (create it if it's missing).
     Or run:  install.cmd "C:\path\to\the\data folder"
  3. On ezpz's Mods page, tick EZPZ Server Tools, save, and restart the server.
  4. Install it for yourself too (double-click install.cmd) so you can join.

ADMINS
  In game, open the Admin panel and click "EZPZ Server Tools" to preview every panel.

UNINSTALL
  Delete the EZPZServerTools folder from %USERPROFILE%\Zomboid\mods (and from the server's
  mods folder, after turning it off on ezpz's Mods page).
'@ -replace "__VERSION__", $version
Set-Content -Path (Join-Path $stage "README.txt") -Value ($readme -replace "`r?`n", "`r`n") -Encoding ASCII

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Output "Built $zip"

# ---- the Workshop upload folder (the game's uploader takes it from here)
if ($Workshop) {
    $ws = Join-Path $env:USERPROFILE "Zomboid\Workshop\EZPZServerTools"
    $contents = Join-Path $ws "Contents\mods\EZPZServerTools"
    if (Test-Path (Join-Path $ws "Contents")) { Remove-Item -Recurse -Force (Join-Path $ws "Contents") }
    New-Item -ItemType Directory -Force (Split-Path $contents) | Out-Null
    Copy-Item -Recurse $mod $contents
    Copy-Item (Join-Path $root "assets\ezpz-256.png") (Join-Path $ws "preview.png") -Force
    $wsTxt = Join-Path $ws "workshop.txt"
    $id = ""
    if (Test-Path $wsTxt) { $id = (Get-Content $wsTxt | Where-Object { $_ -like "id=*" }) -join "" }   # keep the id after the first upload
    $lines = @("version=1")
    if ($id) { $lines += $id }
    $lines += @(
        "title=EZPZ Server Tools",
        "description=Companion mod for ezpz, the Project Zomboid (Build 42) server manager: https://github.com/kruchone/ezpz",
        "description=",
        "description=[h2]For players[/h2]",
        "description=[list][*]Restart and wipe countdowns, just above your hotbar[*]The season ladder: your rank, your categories and the top 5[*]THE END IS NEAR before a season-ending wipe[*]A notice when your reward from last season arrives[/list]",
        "description=[h2]For admins[/h2]",
        "description=[list][*]Each player's health, injuries, moodles and position on the ezpz dashboard[*]A preview of every panel, from the game's Admin panel[/list]",
        "description=",
        "description=No items, no gameplay changes. Needs ezpz on the server PC to be useful.",
        "tags=Build 42;Multiplayer;Interface;Misc",
        "visibility=unlisted"
    )
    [IO.File]::WriteAllLines($wsTxt, [string[]]$lines, (New-Object Text.UTF8Encoding $false))   # no BOM: the uploader reads "version=1" from the first line
    Write-Output "Staged the Workshop upload in $ws (visibility: unlisted; change it in the uploader when you're ready)"
}
