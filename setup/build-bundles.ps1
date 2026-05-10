<#
.SYNOPSIS
  Build the three release bundles (raw plugin zip, Mac tar.gz, Windows zip).

.DESCRIPTION
  Replaces the old inline scripting that used PowerShell's Compress-Archive,
  which writes zip entries with backslash separators ('CupheadCoop\X.dll').
  macOS ditto interprets those literally and never creates the directory
  structure, so the plugin DLLs land at unexpected paths and the install is
  silently broken. This script builds zips via System.IO.Compression with
  forward-slash entries, the cross-platform standard.

.EXAMPLE
  pwsh ./setup/build-bundles.ps1 -Version 0.6.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-ZipForwardSlash {
    param([string]$SourceDir, [string]$ZipPath)
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    $base = (Resolve-Path $SourceDir).Path
    foreach ($f in Get-ChildItem $SourceDir -Recurse -File) {
        $rel = $f.FullName.Substring($base.Length).TrimStart([char]'\', [char]'/').Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $zip.Dispose()
}

$root  = (Resolve-Path "$PSScriptRoot\..").Path
$stage = "$env:TEMP\cupcoop-bundle-$Version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

# 1. Plugin zip — drop CupheadCoop/{CupheadCoop.dll, LiteNetLib.dll}
$pluginStage = "$stage\plugin"
New-Item -ItemType Directory -Force -Path "$pluginStage\CupheadCoop" | Out-Null
Copy-Item "$root\CupheadCoop\bin\Release\CupheadCoop.dll" "$pluginStage\CupheadCoop\"
Copy-Item "$root\CupheadCoop\bin\Release\LiteNetLib.dll"  "$pluginStage\CupheadCoop\"
$pluginZip = "$root\dist\CupheadCoop-v$Version.zip"
New-ZipForwardSlash -SourceDir $pluginStage -ZipPath $pluginZip
Write-Host "[ok] Plugin zip:    $pluginZip"

# 2. Mac tar.gz — setup-mac.sh + plugin zip inside CupheadCoop-mac/
$macStage = "$stage\mac\CupheadCoop-mac"
New-Item -ItemType Directory -Force -Path $macStage | Out-Null
Copy-Item "$root\setup\setup-mac.sh" $macStage
Copy-Item $pluginZip $macStage
$macTgz = "$root\dist\CupheadCoop-mac-v$Version.tar.gz"
if (Test-Path $macTgz) { Remove-Item $macTgz }
& tar -czf $macTgz -C "$stage\mac" "CupheadCoop-mac"
Write-Host "[ok] Mac bundle:    $macTgz"

# 3. Windows bundle — installer scripts + plugin zip inside CupheadCoop-windows/
$winStage = "$stage\win\CupheadCoop-windows"
New-Item -ItemType Directory -Force -Path $winStage | Out-Null
Copy-Item "$root\setup\setup-windows.ps1" $winStage
Copy-Item "$root\setup\setup-windows.bat" $winStage
Copy-Item "$root\setup\INSTALL-windows.txt" "$winStage\README.txt"
Copy-Item $pluginZip $winStage
$winZip = "$root\dist\CupheadCoop-windows-v$Version.zip"
New-ZipForwardSlash -SourceDir "$stage\win" -ZipPath $winZip
Write-Host "[ok] Windows bundle: $winZip"

# Sanity-check the plugin zip's entry layout — must use forward slashes.
$z = [System.IO.Compression.ZipFile]::OpenRead($pluginZip)
foreach ($e in $z.Entries) {
    if ($e.FullName -match '\\') {
        $z.Dispose()
        throw "FATAL: zip entry has backslash separator: $($e.FullName). macOS ditto won't extract correctly."
    }
}
$z.Dispose()
Write-Host "[ok] Verified zip entries use forward slashes"
