<#
.SYNOPSIS
    CupheadCoop installer for Windows.

.DESCRIPTION
    Installs BepInEx 5.4.23.5 (win_x64) into the user's Cuphead folder, drops
    the CupheadCoop plugin, pre-seeds leif.cupheadcoop.cfg with the host IP,
    and enables the BepInEx console.

    Unlike the Mac side, no Steam launch options are needed -- BepInEx on
    Windows hijacks winhttp.dll automatically.

.PARAMETER HostIp
    LAN or ZeroTier address of the host PC (the one running F9 to host).

.PARAMETER CupheadDir
    Optional explicit path to the Cuphead install folder. Auto-detected if
    omitted.

.EXAMPLE
    .\setup-windows.ps1 192.168.0.4
    .\setup-windows.ps1 10.242.74.251 "D:\Games\Steam\steamapps\common\Cuphead"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$HostIp,

    [Parameter(Position=1)]
    [string]$CupheadDir = ""
)

$ErrorActionPreference = "Stop"
$Port        = 47777
$ConnectKey  = "cuphead-coop-v0"
$BepInExUrl  = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Tmp         = Join-Path $env:TEMP "cupcoop-install-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force -Path $Tmp | Out-Null

function Cleanup { Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue }

try {
    # 1. Locate the plugin payload. Accept either a zip next to the script or
    #    an already-extracted CupheadCoop folder (in case the user unzipped it).
    $pluginZip = $null
    $pluginDir = $null
    $zipCandidate = Get-ChildItem -Path $ScriptDir -Filter "CupheadCoop-v*.zip" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($zipCandidate) { $pluginZip = $zipCandidate.FullName }

    if (-not $pluginZip) {
        foreach ($try in @(
            (Join-Path $ScriptDir "CupheadCoop"),
            (Join-Path $ScriptDir "CupheadCoop-v0.1.0\CupheadCoop")
        )) {
            if ((Test-Path $try -PathType Container) -and (Test-Path (Join-Path $try "CupheadCoop.dll"))) {
                $pluginDir = $try
                break
            }
        }
        if (-not $pluginDir) {
            $wrapper = Get-ChildItem -Path $ScriptDir -Filter "CupheadCoop-v*" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($wrapper) {
                $inner = Join-Path $wrapper.FullName "CupheadCoop"
                if ((Test-Path $inner -PathType Container) -and (Test-Path (Join-Path $inner "CupheadCoop.dll"))) {
                    $pluginDir = $inner
                }
            }
        }
    }
    if (-not $pluginZip -and -not $pluginDir) {
        throw "Couldn't find the plugin payload next to this script. Expected CupheadCoop-v0.1.0.zip or a CupheadCoop\ folder containing CupheadCoop.dll."
    }

    # 2. Locate Cuphead.
    if ([string]::IsNullOrWhiteSpace($CupheadDir)) {
        $candidates = New-Object System.Collections.Generic.List[string]
        [void]$candidates.Add("C:\Program Files (x86)\Steam\steamapps\common\Cuphead")
        [void]$candidates.Add("C:\Program Files\Steam\steamapps\common\Cuphead")

        # Walk libraryfolders.vdf to catch non-default Steam libraries (e.g. F:\SteamLibrary).
        $vdfPaths = @(
            "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf",
            "C:\Program Files\Steam\steamapps\libraryfolders.vdf"
        )
        foreach ($vdf in $vdfPaths) {
            if (Test-Path $vdf) {
                $raw = Get-Content $vdf -Raw
                foreach ($m in [regex]::Matches($raw, '"path"\s+"([^"]+)"')) {
                    $libRoot = $m.Groups[1].Value -replace '\\\\', '\'
                    [void]$candidates.Add((Join-Path $libRoot "steamapps\common\Cuphead"))
                }
                break
            }
        }

        foreach ($c in $candidates) {
            if ((Test-Path $c -PathType Container) -and (Test-Path (Join-Path $c "Cuphead.exe"))) {
                $CupheadDir = $c
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($CupheadDir) -or -not (Test-Path (Join-Path $CupheadDir "Cuphead.exe"))) {
        throw "Couldn't auto-find Cuphead. Pass the install folder as the second arg, e.g.:`n  .\setup-windows.ps1 $HostIp `"D:\Games\Steam\steamapps\common\Cuphead`""
    }
    Write-Host "[ok] Cuphead at: $CupheadDir"

    # 3. Install BepInEx if missing.
    $bepCore  = Join-Path $CupheadDir "BepInEx\core"
    $winhttp  = Join-Path $CupheadDir "winhttp.dll"
    if (-not ((Test-Path $bepCore) -and (Test-Path $winhttp))) {
        Write-Host "[..] Downloading BepInEx 5.4.23.5 (win_x64)..."
        $bepZip = Join-Path $Tmp "bep.zip"
        Invoke-WebRequest -UseBasicParsing -Uri $BepInExUrl -OutFile $bepZip
        $bepExtract = Join-Path $Tmp "bep"
        Expand-Archive -Path $bepZip -DestinationPath $bepExtract -Force
        Copy-Item -Path (Join-Path $bepExtract "*") -Destination $CupheadDir -Recurse -Force
        Write-Host "[ok] BepInEx installed"
    } else {
        Write-Host "[ok] BepInEx already present"
    }

    # 4. Drop the plugin.
    $pluginsDir = Join-Path $CupheadDir "BepInEx\plugins"
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    $dst = Join-Path $pluginsDir "CupheadCoop"
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }

    if ($pluginZip) {
        Expand-Archive -Path $pluginZip -DestinationPath $pluginsDir -Force
        Write-Host "[ok] Plugin installed (from zip): $dst"
    } else {
        Copy-Item -Recurse -Path $pluginDir -Destination $dst
        Write-Host "[ok] Plugin installed (from folder): $dst"
    }

    # 5. Pre-write the plugin config.
    $configDir = Join-Path $CupheadDir "BepInEx\config"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $cfg = @"
## CupheadCoop config -- pre-seeded by setup-windows.ps1.

[Debug]
ForceP2WalkRight = false
Verbose = false

[Hotkeys]
Host = F9
Connect = F10
Disconnect = F11
ToggleOverlay = O

[Input]
SendRateHz = 60
BufferFrames = 2

[State]
SendRateHz = 30

[Network]
RemoteHost = $HostIp
Port = $Port
ConnectKey = $ConnectKey
"@
    Set-Content -Path (Join-Path $configDir "leif.cupheadcoop.cfg") -Value $cfg -Encoding UTF8
    Write-Host "[ok] Plugin config written (RemoteHost=$HostIp, Port=$Port)"

    # 6. Enable BepInEx console (helpful for live debugging).
    $bepCfg = Join-Path $configDir "BepInEx.cfg"
    if (Test-Path $bepCfg) {
        $lines = Get-Content $bepCfg
        $inSection = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\[Logging\.Console\]') { $inSection = $true; continue }
            if ($inSection -and $lines[$i] -match '^\[') { $inSection = $false }
            if ($inSection -and $lines[$i] -eq 'Enabled = false') { $lines[$i] = 'Enabled = true' }
        }
        Set-Content -Path $bepCfg -Value $lines -Encoding UTF8
        Write-Host "[ok] BepInEx console enabled"
    }

    Write-Host @"

----------------------------------------
All set. Launch Cuphead from Steam normally -- no launch options needed on
Windows (BepInEx auto-injects via winhttp.dll).

In-game:
  - Start a single-player game.
  - Press F10 to connect to ${HostIp}:${Port}.
  - Top-left overlay should switch to mode=Client; the BepInEx console will
    log "CoopClient: dialing ..." then "CoopClient: handshake ok".
  - Press F11 to disconnect when done.

If Windows Firewall prompts on the host PC the first time it binds the port,
allow it on Private networks.
----------------------------------------
"@
}
finally {
    Cleanup
}
