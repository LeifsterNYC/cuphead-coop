<#
.SYNOPSIS
  Launches the second Cuphead instance (Cuphead-client/) with Goldberg's
  steam_api64.dll stub, bypassing Steam's "game already running" lock.

.DESCRIPTION
  Workflow for solo two-instance testing of CupheadCoop on a single Steam license:
   1. Launch Cuphead via Steam normally — first instance.
   2. Run this script — launches the Cuphead-client/ copy directly.
   3. F9 in the first instance, F10 in the second.

  The Cuphead-client/ folder has its own steam_api64.dll replaced with a
  Goldberg stub built from source (lib/bepinex-5.4.22/.. wait wrong dir,
  see goldberg_emulator/build/Release/steam_api64.dll for source). This
  lets it launch without contacting Steam.exe at all.
#>

$client = "F:\SteamLibrary\steamapps\common\Cuphead-client\Cuphead.exe"
if (-not (Test-Path $client)) {
    Write-Error "Cuphead-client not found at $client. Run setup-cuphead-client.ps1 first?"
    exit 1
}
Write-Host "Launching second Cuphead instance from $client"
Start-Process -FilePath $client -WorkingDirectory (Split-Path $client)
Write-Host "Done. Press F10 inside the new window to connect to 127.0.0.1:47777."
