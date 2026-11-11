# Builds the release packages from the current build outputs.
#   Release\ResoniteFSR3-<version>.zip                (mod package, Thunderstore layout: mod managers can import it)
#   Release\ResoniteFSR3-vrperfkit-addon-<version>.zip (manual dxgi.dll add-on)
# Zips are written with .NET ZipFile and forward-slash entry names (Compress-Archive uses backslashes, which
# Thunderstore / mod managers don't accept).
param(
    [string]$Version = "0.1.0",
    # Built from the VRPerfKit_RSF fork, expected next to this repository by default
    [string]$VrPerfKitDll = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "VRPerfKit_RSF\build\Release\dxgi.dll")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseDirectory = Join-Path $projectRoot "Release"
$packageDirectory = Join-Path $releaseDirectory "ResoniteFSR3"
$addonDirectory = Join-Path $releaseDirectory "ResoniteFSR3-vrperfkit-addon"

# --- Thunderstore package staging
$rendererPluginDirectory = Join-Path $packageDirectory "Renderer\BepInEx\plugins\ResoniteFSR3"
$settingsPluginDirectory = Join-Path $packageDirectory "plugins\ResoniteFSR3.Settings"
New-Item -ItemType Directory -Force $rendererPluginDirectory, $settingsPluginDirectory | Out-Null
Copy-Item (Join-Path $projectRoot "Plugin\bin\Release\net472\ResoniteFSR3.dll") $rendererPluginDirectory -Force
Copy-Item (Join-Path $projectRoot "Build\resonitefsr3.shaders") $rendererPluginDirectory -Force
Copy-Item (Join-Path $projectRoot "SettingsPlugin\bin\Release\net10.0\ResoniteFSR3.Settings.dll") $settingsPluginDirectory -Force
foreach ($file in "README.md", "CHANGELOG.md", "LICENSE", "icon.png") {
    Copy-Item (Join-Path $projectRoot $file) $packageDirectory -Force
}

$manifestPath = Join-Path $packageDirectory "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version_number = $Version
# Thunderstore rejects a manifest with a UTF-8 byte-order mark, which Set-Content -Encoding UTF8 adds in Windows PowerShell
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))

# --- Add-on staging (yml / readme / license are maintained in the staging folder)
Copy-Item $VrPerfKitDll (Join-Path $addonDirectory "dxgi.dll") -Force

function New-ForwardSlashZip([string]$SourceDirectory, [string]$ZipPath) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourceRoot = (Resolve-Path $SourceDirectory).Path.TrimEnd('\') + '\'
        Get-ChildItem $SourceDirectory -Recurse -File | ForEach-Object {
            $entryName = $_.FullName.Substring($sourceRoot.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

$packageZip = Join-Path $releaseDirectory "ResoniteFSR3-$Version.zip"
$addonZip = Join-Path $releaseDirectory "ResoniteFSR3-vrperfkit-addon-$Version.zip"
New-ForwardSlashZip $packageDirectory $packageZip
New-ForwardSlashZip $addonDirectory $addonZip

foreach ($zipPath in $packageZip, $addonZip) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        "== $(Split-Path $zipPath -Leaf) ($([Math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"
        $archive.Entries | ForEach-Object { "   {0,-70} {1,10}" -f $_.FullName, $_.Length }
    }
    finally {
        $archive.Dispose()
    }
}
