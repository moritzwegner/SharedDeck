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
    # GitHub release assets may be at most 2 GiB: the aircraft is split into parts, each a complete ZIP
    $partLimit = 1700MB
    $out = Join-Path (Split-Path -Parent $root) 'vendor\flybywire-aircraft\fbw-a32nx\out'
    Get-ChildItem $dist -Filter 'SharedDeck-A32NX*.zip' | Remove-Item
    Write-Host 'Flugzeug packen (dauert einige Minuten) ...'

    $parts = @()
    $stream = $null
    $zip = $null
    function Open-Part {
        $script:parts += Join-Path $dist "SharedDeck-A32NX-part$($script:parts.Count + 1).tmp"
        $script:stream = [System.IO.File]::Open($script:parts[-1], 'CreateNew')
        $script:zip = New-Object System.IO.Compression.ZipArchive($script:stream, 'Create')
    }
    Open-Part
    try {
        foreach ($package in 'flybywire-aircraft-a320-neo', 'flybywire-aircraft-a320-neo-lock-highlight') {
            Get-ChildItem (Join-Path $out $package) -Recurse -File | ForEach-Object {
                if ($stream.Length -gt $partLimit) {
                    $zip.Dispose()
                    Open-Part
                }
                $relative = $_.FullName.Substring($out.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $relative, 'Fastest') | Out-Null
            }
        }
    } finally {
        $zip.Dispose()
    }

    for ($i = 0; $i -lt $parts.Count; $i++) {
        $name = "SharedDeck-A32NX-part$($i + 1)of$($parts.Count).zip"
        Move-Item $parts[$i] (Join-Path $dist $name)
        Write-Host "Fertig: $name ($([math]::Round((Get-Item (Join-Path $dist $name)).Length / 1MB)) MB)"
    }
}

# release manifest for the in-app updater: version, aircraft revision and checksums of all release files
Write-Host 'Release-Manifest schreiben'
$version = ([xml](Get-Content (Join-Path $app 'SharedDeck.App\SharedDeck.App.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$parts = @(Get-ChildItem $dist -Filter 'SharedDeck-A32NX-part*of*.zip' | Sort-Object Name)
$aircraftRevision = 0
foreach ($part in $parts) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($part.FullName)
    try {
        $bridge = $zip.Entries | Where-Object { $_.FullName -like '*/A32NX/ExtrasHost/index.js' } | Select-Object -First 1
        if ($bridge) {
            $reader = New-Object System.IO.StreamReader($bridge.Open())
            $match = [regex]::Match($reader.ReadToEnd(), 'SHAREDDECK_AIRCRAFT_REVISION\s*=\s*(\d+)')
            $reader.Dispose()
            if ($match.Success) { $aircraftRevision = [int]$match.Groups[1].Value }
        }
    } finally {
        $zip.Dispose()
    }
}
$files = @(Get-Item (Join-Path $dist 'SharedDeck-Setup.exe')) + $parts | ForEach-Object {
    [ordered]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{ version = $version; aircraftRevision = $aircraftRevision; files = @($files) }
[System.IO.File]::WriteAllText((Join-Path $dist 'shareddeck-release.json'), ($manifest | ConvertTo-Json -Depth 4))
Write-Host "Fertig: shareddeck-release.json (Version $version, Flugzeug-Revision $aircraftRevision, $($files.Count) Dateien)"
