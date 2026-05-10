<#
.SYNOPSIS
  Launches both Cuphead instances (host + client) for solo two-instance testing.

.DESCRIPTION
  Both instances use Goldberg's steam_api64.dll stub. Each launches in proper
  windowed mode (with title bar so you can drag and alt-tab between them),
  and the script positions them side-by-side via Win32 SetWindowPos once the
  windows have rendered.

  Original Steam steam_api64.dll backups (in case you want to revert):
    F:\SteamLibrary\steamapps\common\Cuphead\steam_api64.dll.steam_orig
    F:\SteamLibrary\steamapps\common\Cuphead-client\steam_api64.dll.steam_orig

.PARAMETER Width
  Per-window width in pixels. Default 1100.

.PARAMETER Height
  Per-window height in pixels. Default 700.
#>
[CmdletBinding()]
param(
    [int]$Width  = 1100,
    [int]$Height = 700
)

Add-Type -Namespace Win32 -Name User32 -MemberDefinition @"
[DllImport("user32.dll")]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
"@ -ErrorAction SilentlyContinue

$hostExe   = "F:\SteamLibrary\steamapps\common\Cuphead\Cuphead.exe"
$clientExe = "F:\SteamLibrary\steamapps\common\Cuphead-client\Cuphead.exe"
$argList   = @("-screen-fullscreen", "0", "-screen-width", "$Width", "-screen-height", "$Height")

if (-not (Test-Path $hostExe))   { Write-Error "Host Cuphead.exe not found at $hostExe";     exit 1 }
if (-not (Test-Path $clientExe)) { Write-Error "Client Cuphead.exe not found at $clientExe"; exit 1 }

function Wait-And-Position {
    param([System.Diagnostics.Process]$proc, [int]$x, [int]$y, [int]$w, [int]$h)
    # Wait up to 15s for the window handle to appear (Cuphead's splash + Unity load take a few seconds).
    for ($i = 0; $i -lt 150; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 200   # let Unity finish setting initial size
            [void][Win32.User32]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $x, $y, $w, $h, 0x0040)  # SWP_SHOWWINDOW
            return $true
        }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

Write-Host "Launching host instance ($Width x $Height)..."
$hostProc = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -ArgumentList $argList -PassThru
Start-Sleep -Milliseconds 1500
Write-Host "Launching client instance ($Width x $Height)..."
$clientProc = Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe) -ArgumentList $argList -PassThru

# Position once both windows have rendered.
$gap = 20
$hostX   = 50
$clientX = $hostX + $Width + $gap
$y       = 50

if (Wait-And-Position $hostProc   $hostX   $y $Width $Height) { Write-Host "[ok] host positioned at ($hostX,$y)" }
else { Write-Warning "Host window handle never appeared; you'll have to position it manually." }

if (Wait-And-Position $clientProc $clientX $y $Width $Height) { Write-Host "[ok] client positioned at ($clientX,$y)" }
else { Write-Warning "Client window handle never appeared; you'll have to position it manually." }

Write-Host ""
Write-Host "Both Cuphead windows running. Focus the one you want input to go to."
Write-Host "  F9   host (in host window)"
Write-Host "  F10  connect (in client window)"
Write-Host "  F11  disconnect"
Write-Host "  O    toggle overlay"
