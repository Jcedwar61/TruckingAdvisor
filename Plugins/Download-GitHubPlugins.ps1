# Download-GitHubPlugins.ps1
# Downloads the RenCloud scs-sdk-plugin (pinned release) and installs the 64-bit scs-telemetry.dll
# into American Truck Simulator / Euro Truck Simulator 2.
#
#   - Pinned to the plugin layout TruckingAdvisor reads (never "latest").
#   - Verifies the SHA-256 when one is set in plugin_manifest.json; otherwise shows it and asks.
#   - Backs up any existing plugin DLL before replacing it.
#   - Extracts only the DLL to a fixed path (no paths taken from the archive).
#
# Run directly:          .\Download-GitHubPlugins.ps1
# Use from another script: . .\Download-GitHubPlugins.ps1 -LoadOnly   (defines the functions only)

param(
    [string]$StagingDir = (Join-Path $env:LOCALAPPDATA "TruckingAdvisor\Plugins"),
    [switch]$Yes,
    [switch]$LoadOnly
)

$script:PluginManifest = Get-Content -Raw -Path (Join-Path $PSScriptRoot "plugin_manifest.json") | ConvertFrom-Json
$script:Plugin = $script:PluginManifest.plugins | Where-Object { $_.id -eq "scs-sdk-plugin" } | Select-Object -First 1

function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    $add = { param($p) if ($p) { $p = $p.Replace('/', '\').TrimEnd('\'); if (-not $roots.Contains($p)) { $roots.Add($p) } } }
    try { & $add (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -Name SteamPath -ErrorAction Stop).SteamPath } catch {}
    & $add "C:\Program Files (x86)\Steam"
    & $add "C:\Program Files\Steam"
    foreach ($root in @($roots)) {
        $vdf = [IO.Path]::Combine($root, "steamapps", "libraryfolders.vdf")
        if (Test-Path -LiteralPath $vdf -ErrorAction SilentlyContinue) {
            foreach ($m in [regex]::Matches((Get-Content -Raw $vdf), '"path"\s+"([^"]+)"')) {
                & $add ($m.Groups[1].Value.Replace('\\', '\'))
            }
        }
    }
    return $roots
}

function Get-GameBinFolders {
    foreach ($lib in Get-SteamLibraries) {
        foreach ($game in @("American Truck Simulator", "Euro Truck Simulator 2")) {
            $bin = [IO.Path]::Combine($lib, "steamapps", "common", $game, "bin", "win_x64")
            if (Test-Path -LiteralPath $bin -ErrorAction SilentlyContinue) { $bin }
        }
    }
}

# Downloads the pinned release. Returns @{ Dll; Sha256; Verified; AssetName }.
function Get-TelemetryPlugin([string]$StagingDir) {
    $tag = $script:Plugin.releaseTag
    $api = "https://api.github.com/repos/$($script:Plugin.ownerRepo)/releases/tags/$tag"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "TruckingAdvisor/2.2" } -TimeoutSec 30
    $asset = $release.assets | Where-Object { $_.name -like "*.zip" -or $_.name -like "*.dll" } | Select-Object -First 1
    if (-not $asset) { throw "No .zip or .dll asset found in release $tag." }

    New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
    $download = Join-Path ([IO.Path]::GetTempPath()) ("ta_plugin_" + [guid]::NewGuid().ToString("N") + [IO.Path]::GetExtension($asset.name))
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $download -UseBasicParsing -TimeoutSec 60
    $sha = (Get-FileHash -Algorithm SHA256 -Path $download).Hash.ToLowerInvariant()

    $expected = "$($script:Plugin.sha256)".ToLowerInvariant()
    if ($expected -and $sha -ne $expected) {
        Remove-Item $download -Force
        throw "Checksum mismatch for $($asset.name): got $sha, expected $expected. Nothing was installed."
    }

    $dll = Join-Path $StagingDir $script:Plugin.targetDll
    if ($asset.name -like "*.dll") {
        Copy-Item -Path $download -Destination $dll -Force
    } else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($download)
        try {
            $entry = $zip.Entries | Where-Object { $_.Name -ieq $script:Plugin.targetDll -and $_.FullName -match '(x64|win64|win_x64)' } | Select-Object -First 1
            if (-not $entry) { throw "No 64-bit $($script:Plugin.targetDll) inside $($asset.name)." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
        } finally { $zip.Dispose() }
    }
    Remove-Item $download -Force -ErrorAction SilentlyContinue
    return @{ Dll = $dll; Sha256 = $sha; Verified = [bool]$expected; AssetName = $asset.name }
}

# Copies the staged DLL into each game. Returns one message per game folder.
function Install-TelemetryPlugin([string]$StagedDll) {
    $bins = @(Get-GameBinFolders)
    if ($bins.Count -eq 0) { return @("No ATS/ETS2 install found. Copy $StagedDll into <game>\bin\win_x64\plugins by hand.") }
    foreach ($bin in $bins) {
        $pluginDir = Join-Path $bin "plugins"
        $dest = Join-Path $pluginDir $script:Plugin.targetDll
        try {
            New-Item -ItemType Directory -Force -Path $pluginDir -ErrorAction Stop | Out-Null
            if (Test-Path $dest) {
                $backup = "$dest.bak-$(Get-Date -Format yyyyMMddHHmmss)"
                Copy-Item -Path $dest -Destination $backup -ErrorAction Stop
                "Backed up existing plugin -> $backup"
            }
            Copy-Item -Path $StagedDll -Destination $dest -Force -ErrorAction Stop
            "Installed -> $dest"
        } catch [System.UnauthorizedAccessException] {
            "Permission denied for $pluginDir. Run this script as administrator, or copy $StagedDll there by hand."
        } catch {
            "Could not write ${dest}: $($_.Exception.Message) (close the game and try again)"
        }
    }
}

if ($LoadOnly) { return }

$ErrorActionPreference = "Stop"
Write-Host "Downloading $($script:Plugin.ownerRepo) $($script:Plugin.releaseTag)..."
$result = Get-TelemetryPlugin -StagingDir $StagingDir
Write-Host "Staged $($result.AssetName) -> $($result.Dll)"
Write-Host "SHA-256: $($result.Sha256)"
if (-not $result.Verified -and -not $Yes) {
    Write-Host "No published checksum is configured; compare the hash with $($script:Plugin.releasesUrl) if you want to verify."
    if ((Read-Host "Install it? (y/N)") -notmatch '^(y|yes)$') { Write-Host "Cancelled. Nothing was installed."; return }
}
Install-TelemetryPlugin -StagedDll $result.Dll | ForEach-Object { Write-Host $_ }
