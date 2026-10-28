# Builds StellarMap.dll and installs it into the game (Mods\ + UserData\StellarMap\www\).
param(
    [string]$Game = "D:\SteamLibrary\steamapps\common\StellarDrive Demo",
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
$csc = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe",
                     "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $csc) { throw "Roslyn csc.exe not found (install Visual Studio Build Tools)." }

$managed = Join-Path $Game "StellarDrive_Data\Managed"
$refs = @(
    "$managed\mscorlib.dll", "$managed\netstandard.dll", "$managed\System.dll", "$managed\System.Core.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.IMGUIModule.dll",
    "$managed\UnityEngine.TextRenderingModule.dll", "$managed\Newtonsoft.Json.dll",
    (Join-Path $Game "MelonLoader\net35\MelonLoader.dll")
) | ForEach-Object { "-r:$_" }

$out = Join-Path $PSScriptRoot "bin\StellarMap.dll"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
& $csc.FullName -nologo -nostdlib -target:library -optimize -langversion:latest -nowarn:CS1701,CS1702 `
    "-out:$out" @refs (Join-Path $PSScriptRoot "StellarMap.cs")
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Built $out"

if (-not $NoInstall) {
    Copy-Item $out (Join-Path $Game "Mods\StellarMap.dll") -Force
    $www = Join-Path $Game "UserData\StellarMap\www"
    New-Item -ItemType Directory -Force $www | Out-Null
    # All pages of the site (live map, station model...), so links between them also work locally.
    Get-ChildItem (Join-Path $PSScriptRoot "..") -Filter *.html | Copy-Item -Destination $www -Force
    Write-Host "Installed into $Game"
}
