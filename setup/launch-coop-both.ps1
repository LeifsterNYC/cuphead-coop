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

function Start-PersistentReposition {
    # Re-applies SetWindowPos every 500ms for 15s in a background runspace, so the position
    # sticks regardless of when Unity finishes its own startup window-init (which can override
    # whatever we set if we only call once). Using SWP_NOZORDER | SWP_NOACTIVATE so we don't
    # steal focus or rearrange Z-order each tick.
    param([int]$pid_, [int]$x, [int]$y, [int]$w, [int]$h)
    $script = {
        param($pid_, $x, $y, $w, $h)
        Add-Type -Namespace Win32 -Name User32 -MemberDefinition @"
[DllImport("user32.dll")]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
"@
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline) {
            try {
                $p = Get-Process -Id $pid_ -ErrorAction Stop
                if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
                    [void][Win32.User32]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $x, $y, $w, $h, 0x0014)  # SWP_NOZORDER | SWP_NOACTIVATE
                }
            } catch { return }
            Start-Sleep -Milliseconds 500
        }
    }
    Start-Job -ScriptBlock $script -ArgumentList $pid_, $x, $y, $w, $h | Out-Null
}

Write-Host "Launching host instance ($Width x $Height)..."
$hostProc = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -ArgumentList $argList -PassThru
Start-Sleep -Milliseconds 1500
Write-Host "Launching client instance ($Width x $Height)..."
$clientProc = Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe) -ArgumentList $argList -PassThru

# Position both via background reposition jobs that re-apply SetWindowPos for 15s so the
# positions stick even if Unity's own window-init code runs after our first call.
$gap = 20
$hostX   = 50
$clientX = $hostX + $Width + $gap
$y       = 50

Start-PersistentReposition -pid_ $hostProc.Id   -x $hostX   -y $y -w $Width -h $Height
Start-PersistentReposition -pid_ $clientProc.Id -x $clientX -y $y -w $Width -h $Height
Write-Host "[ok] reposition jobs running for 15s; windows should snap into place once Unity finishes loading"

Write-Host ""
Write-Host "Both Cuphead windows running. Focus the one you want input to go to."
Write-Host "  F9   host (in host window)"
Write-Host "  F10  connect (in client window)"
Write-Host "  F11  disconnect"
Write-Host "  O    toggle overlay"
