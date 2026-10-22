# Builds dist\SharedDeck-Setup.exe (app + SharedDeck module) and, with -Aircraft, dist\SharedDeck-A32NX.zip.
# Prerequisites: .NET 8 SDK; the module in package\shareddeck-module (wasm\build.sh) and, for -Aircraft,
# the built FBW fork in vendor\flybywire-aircraft\fbw-a32nx\out.
param(
    [switch]$Aircraft
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $root 'app'
$module = Join-Path $root 'package\shareddeck-module'
$payloadDir = Join-Path $app 'SharedDeck.Setup\payload'
$payload = Join-Path $payloadDir 'payload.zip'

if (-not (Test-Path (Join-Path $module 'modules\shareddeck-module.wasm'))) {
    throw "SharedDeck-Modul fehlt: zuerst wasm\build.sh ausführen."
}

Write-Host '1/3 App veröffentlichen'
dotnet publish (Join-Path $app 'SharedDeck.App') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $dist 'build\app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (App) fehlgeschlagen' }

Write-Host '2/3 Payload packen'
New-Item -ItemType Directory -Force $payloadDir | Out-Null
if (Test-Path $payload) { Remove-Item $payload }
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $dist 'build\app\SharedDeck.App.exe'), 'app/SharedDeck.App.exe', 'Optimal') | Out-Null
    Get-ChildItem $module -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($module.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, "module/shareddeck-module/$relative", 'Optimal') | Out-Null
    }
} finally {
    $zip.Dispose()
}

Write-Host '3/3 Setup veröffentlichen'
dotnet publish (Join-Path $app 'SharedDeck.Setup') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $dist 'build\setup')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (Setup) fehlgeschlagen' }
Copy-Item (Join-Path $dist 'build\setup\SharedDeck-Setup.exe') (Join-Path $dist 'SharedDeck-Setup.exe') -Force
Write-Host "Fertig: $(Join-Path $dist 'SharedDeck-Setup.exe')"

if ($Aircraft) {
    $out = Join-Path (Split-Path -Parent $root) 'vendor\flybywire-aircraft\fbw-a32nx\out'
    $aircraftZip = Join-Path $dist 'SharedDeck-A32NX.zip'
    Write-Host "Flugzeug packen nach $aircraftZip (dauert einige Minuten) ..."
    if (Test-Path $aircraftZip) { Remove-Item $aircraftZip }
    $zip = [System.IO.Compression.ZipFile]::Open($aircraftZip, 'Create')
    try {
        foreach ($package in 'flybywire-aircraft-a320-neo', 'flybywire-aircraft-a320-neo-lock-highlight') {
            $packageDir = Join-Path $out $package
            Get-ChildItem $packageDir -Recurse -File | ForEach-Object {
                $relative = $_.FullName.Substring($out.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $relative, 'Fastest') | Out-Null
            }
        }
    } finally {
        $zip.Dispose()
    }
    Write-Host "Fertig: $aircraftZip"
}
