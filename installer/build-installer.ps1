# Builds publish\bin\TruckingAdvisor.exe and publish\TruckingAdvisor-Setup-<version>.exe on Windows.
# Needs the .NET 8 SDK and NSIS 3 (winget install NSIS.NSIS, or choco install nsis).
param([string]$Version = "2.5.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  dotnet publish src/TruckingAdvisor.UI/TruckingAdvisor.UI.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o publish/bin
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

  # Everything the script installs has to exist in the publish output; a missing file makes makensis
  # abort on a "no files found" warning that says nothing about the cause.
  $published = @("TruckingAdvisor.exe", "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll",
    "vcruntime140_cor3.dll", "wpfgfx_cor3.dll", "e_sqlite3.dll", "onnxruntime.dll", "sherpa-onnx-c-api.dll",
    "runtimes\win-x64\whisper.dll")
  $missing = @($published | Where-Object { -not (Test-Path (Join-Path "publish\bin" $_)) })
  $missing += @("Assets\TruckingAdvisor.ico", "README.md") | Where-Object { -not (Test-Path $_) }
  if ($missing) { throw "installer inputs are missing: $($missing -join ', ')" }
  $makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
  if (-not $makensis) { $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
  if (-not $makensis) { throw "NSIS not found. Install it with: winget install NSIS.NSIS" }
  & $makensis /V2 "/DVERSION=$Version" "/DPUBLISH_DIR=..\publish\bin" "/DOUT_DIR=..\publish" installer\TruckingAdvisor.nsi
  if ($LASTEXITCODE -ne 0) { throw "makensis failed" }
  Get-Item "publish\TruckingAdvisor-Setup-$Version.exe"
} finally { Pop-Location }
