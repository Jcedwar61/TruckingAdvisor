# Install_TruckingAdvisor.ps1 - TruckingAdvisor setup (per-user, no admin needed).
#
# What it does, in order:
#   1. Copies the package into %LOCALAPPDATA%\Programs\TruckingAdvisor (or the folder you pick).
#   2. Builds the app:
#        - Full app (WPF, live telemetry, AI CB radio) with the .NET 8 SDK if it is installed, or uses
#          a prebuilt bin\TruckingAdvisor.exe if the package already contains one.
#        - Otherwise the lightweight simulator window (TruckingAdvisor-Lite.exe) with Windows' built-in
#          C# 5 compiler. It does NOT show live telemetry or use AI.
#   3. Optionally downloads the pinned telemetry plugin and asks you to confirm its SHA-256.
#   4. Creates shortcuts.
param(
  [string]$PackageZip = ""
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Windows.Forms.Application]::EnableVisualStyles()

$defaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\TruckingAdvisor"
$form = New-Object Windows.Forms.Form
$form.Text = "TruckingAdvisor Setup"
$form.Size = New-Object Drawing.Size(660, 500)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.BackColor = [Drawing.Color]::FromArgb(13, 18, 28)
$form.ForeColor = [Drawing.Color]::White
try {
  $ico = Join-Path $PSScriptRoot "Assets\TruckingAdvisor.ico"
  if (Test-Path $ico) { $form.Icon = New-Object Drawing.Icon($ico) }
} catch {}

function New-Label([string]$text, [int]$x, [int]$y, [Drawing.Font]$font, [Drawing.Color]$color) {
  $l = New-Object Windows.Forms.Label
  $l.Text = $text; $l.Location = New-Object Drawing.Point($x, $y); $l.AutoSize = $true
  if ($font) { $l.Font = $font }
  if ($color) { $l.ForeColor = $color }
  $form.Controls.Add($l)
}
function New-Check([string]$text, [int]$y, [bool]$checked) {
  $c = New-Object Windows.Forms.CheckBox
  $c.Text = $text; $c.Checked = $checked
  $c.Location = New-Object Drawing.Point(22, $y); $c.Size = New-Object Drawing.Size(590, 22)
  $form.Controls.Add($c); return $c
}

New-Label "TruckingAdvisor - ATS Companion Setup" 22 16 (New-Object Drawing.Font("Segoe UI", 13, [Drawing.FontStyle]::Bold)) ([Drawing.Color]::FromArgb(245, 158, 11))
New-Label "Per-user install. No administrator rights needed (except to write into Steam folders under Program Files)." 22 44 (New-Object Drawing.Font("Segoe UI", 9)) ([Drawing.Color]::FromArgb(148, 163, 184))
New-Label "Installation Directory:" 22 76 (New-Object Drawing.Font("Segoe UI", 9, [Drawing.FontStyle]::Bold)) ([Drawing.Color]::White)

$txtDir = New-Object Windows.Forms.TextBox
$txtDir.Text = $defaultInstallDir
$txtDir.Location = New-Object Drawing.Point(22, 98)
$txtDir.Size = New-Object Drawing.Size(505, 26)
$form.Controls.Add($txtDir)
$btnBrowse = New-Object Windows.Forms.Button
$btnBrowse.Text = "Browse..."
$btnBrowse.Location = New-Object Drawing.Point(535, 96)
$btnBrowse.Size = New-Object Drawing.Size(85, 28)
$btnBrowse.Add_Click({
  $dlg = New-Object Windows.Forms.FolderBrowserDialog
  $dlg.SelectedPath = $txtDir.Text
  if ($dlg.ShowDialog() -eq "OK") { $txtDir.Text = $dlg.SelectedPath }
})
$form.Controls.Add($btnBrowse)

$chkShortcuts = New-Check "Create Desktop and Start Menu shortcuts" 134 $true
$chkPlugin = New-Check "Download the ATS/ETS2 telemetry plugin from GitHub (you'll confirm before it's installed)" 160 $true
$chkLaunch = New-Check "Launch TruckingAdvisor when setup finishes" 186 $true

$txtLog = New-Object Windows.Forms.TextBox
$txtLog.Multiline = $true; $txtLog.ReadOnly = $true; $txtLog.ScrollBars = "Vertical"
$txtLog.BackColor = [Drawing.Color]::FromArgb(9, 13, 20)
$txtLog.ForeColor = [Drawing.Color]::FromArgb(16, 185, 129)
$txtLog.Font = New-Object Drawing.Font("Consolas", 8.5)
$txtLog.Location = New-Object Drawing.Point(22, 218)
$txtLog.Size = New-Object Drawing.Size(598, 175)
$txtLog.Text = "Ready to install TruckingAdvisor."
$form.Controls.Add($txtLog)

function Write-InstallLog([string]$msg) {
  $txtLog.AppendText("`r`n" + $msg)
  [Windows.Forms.Application]::DoEvents()
}

# Extracts a zip, refusing any entry that would land outside $dest (zip-slip).
function Expand-PackageSafely([string]$zipPath, [string]$dest) {
  $root = [IO.Path]::GetFullPath($dest).TrimEnd('\') + '\'
  $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
  try {
    foreach ($entry in $zip.Entries) {
      if ([string]::IsNullOrEmpty($entry.Name)) { continue }
      $target = [IO.Path]::GetFullPath((Join-Path $dest $entry.FullName))
      if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path in package: $($entry.FullName)"
      }
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
  } finally { $zip.Dispose() }
}

function Test-DotnetSdk8 {
  $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $dotnet) { return $false }
  return [bool](& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^(8|9|10)\.' })
}

$btnInstall = New-Object Windows.Forms.Button
$btnInstall.Text = "Install"
$btnInstall.Font = New-Object Drawing.Font("Segoe UI", 10, [Drawing.FontStyle]::Bold)
$btnInstall.BackColor = [Drawing.Color]::FromArgb(245, 158, 11)
$btnInstall.ForeColor = [Drawing.Color]::FromArgb(11, 15, 23)
$btnInstall.FlatStyle = "Flat"
$btnInstall.Location = New-Object Drawing.Point(430, 405)
$btnInstall.Size = New-Object Drawing.Size(190, 40)

$btnInstall.Add_Click({
  $btnInstall.Enabled = $false
  $ErrorActionPreference = "Stop"
  $targetDir = $txtDir.Text.Trim()
  try {
    Write-InstallLog "[1/4] Copying files to $targetDir"
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    if ($PackageZip -and (Test-Path $PackageZip)) {
      Expand-PackageSafely $PackageZip $targetDir
    } elseif ([IO.Path]::GetFullPath($PSScriptRoot) -ne [IO.Path]::GetFullPath($targetDir)) {
      Get-ChildItem -Path $PSScriptRoot -Force | Where-Object { $_.Name -notin @(".git", "bin", "obj", ".vs") } |
        Copy-Item -Destination $targetDir -Recurse -Force
    }

    Write-InstallLog "[2/4] Building TruckingAdvisor..."
    $appExe = $null
    $prebuilt = Join-Path $targetDir "bin\TruckingAdvisor.exe"
    $uiProject = Join-Path $targetDir "src\TruckingAdvisor.UI\TruckingAdvisor.UI.csproj"
    if (Test-Path $prebuilt) {
      $appExe = $prebuilt
      Write-InstallLog "      Using prebuilt full app."
    } elseif ((Test-Path $uiProject) -and (Test-DotnetSdk8)) {
      Write-InstallLog "      .NET SDK found - building the full app (this can take a minute)..."
      $out = & dotnet publish $uiProject -c Release -r win-x64 --self-contained false -o (Join-Path $targetDir "bin") 2>&1
      if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $targetDir "bin\TruckingAdvisor.exe"))) {
        $appExe = Join-Path $targetDir "bin\TruckingAdvisor.exe"
        Write-InstallLog "      Built full app."
      } else {
        Write-InstallLog "      Full app build failed:`r`n$(($out | Select-Object -Last 8) -join "`r`n")"
      }
    }
    if (-not $appExe) {
      $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
      if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
      $liteSource = Join-Path $targetDir "Launcher\TruckingAdvisorDesktopHost.cs"
      $liteExe = Join-Path $targetDir "TruckingAdvisor-Lite.exe"
      $iconPath = Join-Path $targetDir "Assets\TruckingAdvisor.ico"
      if ((Test-Path $csc) -and (Test-Path $liteSource)) {
        Write-InstallLog "      .NET 8 SDK not found - building the lightweight simulator window instead."
        Write-InstallLog "      (It does not read live telemetry or use AI. Install the .NET 8 SDK and re-run setup for the full app.)"
        # System.Speech isn't next to csc.exe; find it in the .NET 4 GAC.
        $speech = Get-ChildItem -Path (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\System.Speech") -Recurse -Filter System.Speech.dll -ErrorAction SilentlyContinue |
          Select-Object -First 1 -ExpandProperty FullName
        if (-not $speech) { $speech = "System.Speech.dll" }
        $cscArgs = @("/nologo", "/target:winexe", "/out:$liteExe",
                     "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:$speech",
                     "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll", $liteSource)
        if (Test-Path $iconPath) { $cscArgs = @("/win32icon:$iconPath") + $cscArgs }
        $out = & $csc @cscArgs 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path $liteExe)) { $appExe = $liteExe; Write-InstallLog "      Built TruckingAdvisor-Lite.exe." }
        else { Write-InstallLog "      Lite build failed:`r`n$(($out | Select-Object -Last 8) -join "`r`n")" }
      } else {
        Write-InstallLog "      No compiler found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download and re-run setup."
      }
    }

    if ($chkPlugin.Checked) {
      Write-InstallLog "[3/4] Telemetry plugin..."
      . (Join-Path $targetDir "Plugins\Download-GitHubPlugins.ps1") -LoadOnly
      try {
        $plugin = Get-TelemetryPlugin -StagingDir (Join-Path $env:LOCALAPPDATA "TruckingAdvisor\Plugins")
        Write-InstallLog "      Downloaded $($plugin.AssetName), SHA-256 $($plugin.Sha256)"
        $ok = $plugin.Verified
        if (-not $ok) {
          $ok = [Windows.Forms.MessageBox]::Show(
            "Downloaded $($plugin.AssetName)`r`nSHA-256: $($plugin.Sha256)`r`n`r`nNo published checksum is configured, so please confirm. You can compare it on the plugin's GitHub release page.`r`n`r`nInstall the plugin into your ATS/ETS2 folders?",
            "Confirm Telemetry Plugin", "YesNo", "Warning") -eq "Yes"
        }
        if ($ok) { Install-TelemetryPlugin -StagedDll $plugin.Dll | ForEach-Object { Write-InstallLog "      $_" } }
        else { Write-InstallLog "      Skipped. Nothing was installed." }
      } catch {
        Write-InstallLog "      Plugin step failed: $($_.Exception.Message). You can install it later from the app."
      }
    }

    if ($chkShortcuts.Checked -and $appExe) {
      Write-InstallLog "[4/4] Creating shortcuts..."
      $wsh = New-Object -ComObject WScript.Shell
      foreach ($folder in @("Desktop", "Programs")) {
        $sc = $wsh.CreateShortcut((Join-Path ([Environment]::GetFolderPath($folder)) "TruckingAdvisor.lnk"))
        $sc.TargetPath = $appExe
        $sc.WorkingDirectory = Split-Path -Parent $appExe
        $icon = Join-Path $targetDir "Assets\TruckingAdvisor.ico"
        if (Test-Path $icon) { $sc.IconLocation = "$icon,0" }
        $sc.Description = "TruckingAdvisor - ATS telemetry, driving coach and AI CB radio"
        $sc.Save()
      }
    }

    if ($appExe) {
      Write-InstallLog "Done. Installed at $targetDir"
      if ($chkLaunch.Checked) { Start-Process -FilePath $appExe -WorkingDirectory (Split-Path -Parent $appExe) }
      [Windows.Forms.MessageBox]::Show("TruckingAdvisor is installed in:`r`n$targetDir", "Setup Complete", "OK", "Information") | Out-Null
      $form.Close()
    } else {
      Write-InstallLog "Setup could not build the app. See the messages above."
      $btnInstall.Enabled = $true
    }
  } catch {
    Write-InstallLog "ERROR: $($_.Exception.Message)"
    $btnInstall.Enabled = $true
  }
})
$form.Controls.Add($btnInstall)
[void]$form.ShowDialog()
