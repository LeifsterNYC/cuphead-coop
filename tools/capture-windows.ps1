<#
.SYNOPSIS
  Screenshots every running Cuphead window to PNG files, labeled host/client.

.DESCRIPTION
  Companion to setup\launch-coop-both.ps1 for automated visual verification:
  an agent launches both instances side-by-side, runs this on an interval, and
  inspects the PNGs to compare what the host and client are rendering.

  Uses Graphics.CopyFromScreen over each window's rect (NOT PrintWindow, which
  returns black frames for GPU-swapchain games like Unity titles). That means
  the windows must be visible and unoccluded — which launch-coop-both.ps1's
  side-by-side placement guarantees.

.PARAMETER OutDir
  Directory for the PNGs. Created if missing.

.PARAMETER Label
  Optional tag inserted into the filename (e.g. the test phase: "prefight").
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Label = "shot"
)

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Win32 -Name Cap -MemberDefinition @"
[DllImport("user32.dll")]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
public struct RECT { public int Left, Top, Right, Bottom; }
"@ -ErrorAction SilentlyContinue

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }

$stamp = Get-Date -Format "HHmmss"
$procs = Get-Process -Name "Cuphead" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
if (-not $procs) { Write-Error "no Cuphead windows found"; exit 1 }

foreach ($p in $procs) {
    # Which install is this? The client copy lives in Cuphead-client.
    $role = if ($p.Path -like "*Cuphead-client*") { "client" } else { "host" }

    $rect = New-Object Win32.Cap+RECT
    if (-not [Win32.Cap]::GetWindowRect($p.MainWindowHandle, [ref]$rect)) { continue }
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { continue }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $gfx.Dispose()

    $file = Join-Path $OutDir ("{0}-{1}-{2}.png" -f $Label, $role, $stamp)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "[ok] $file"
}
