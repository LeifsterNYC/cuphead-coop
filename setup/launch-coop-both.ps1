<#
.SYNOPSIS
  Launches both Cuphead instances (host + client) for solo two-instance testing.

.DESCRIPTION
  Both instances now use Goldberg's steam_api64.dll stub (no Steam involvement at all).
  Both launch in 800x600 windowed mode and are positioned side-by-side via a small
  delay between launches so the OS places them sequentially.

  Use this instead of "launch via Steam, then launch via this script" — symmetric.

  Original Steam steam_api64.dll backups (in case you want to revert to normal play):
    F:\SteamLibrary\steamapps\common\Cuphead\steam_api64.dll.steam_orig
    F:\SteamLibrary\steamapps\common\Cuphead-client\steam_api64.dll.steam_orig

  To revert: rename steam_api64.dll.steam_orig back to steam_api64.dll in each folder.

.PARAMETER Width
  Window width in pixels. Default 800.

.PARAMETER Height
  Window height in pixels. Default 600.
#>
[CmdletBinding()]
param(
    [int]$Width  = 800,
    [int]$Height = 600
)

$host_   = "F:\SteamLibrary\steamapps\common\Cuphead\Cuphead.exe"
$client_ = "F:\SteamLibrary\steamapps\common\Cuphead-client\Cuphead.exe"
$argList = @("-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height", "-popupwindow")

if (-not (Test-Path $host_))   { Write-Error "Host Cuphead.exe not found at $host_";     exit 1 }
if (-not (Test-Path $client_)) { Write-Error "Client Cuphead.exe not found at $client_"; exit 1 }

Write-Host "Launching host instance:   $host_"
Start-Process -FilePath $host_   -WorkingDirectory (Split-Path $host_)   -ArgumentList $argList
Start-Sleep -Milliseconds 1500   # let the first window claim its position before the second starts
Write-Host "Launching client instance: $client_"
Start-Process -FilePath $client_ -WorkingDirectory (Split-Path $client_) -ArgumentList $argList

Write-Host ""
Write-Host "Both windows started in $Width x ${Height} windowed mode."
Write-Host "Drag them side-by-side, focus the host window, F9 to start hosting."
Write-Host "Then focus the client window, F10 to connect."
Write-Host ""
Write-Host "Hotkeys (focused window only — alt-tab to switch which cup you control):"
Write-Host "  F9   host"
Write-Host "  F10  connect"
Write-Host "  F11  disconnect"
Write-Host "  O    toggle overlay"
