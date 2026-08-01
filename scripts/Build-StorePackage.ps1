param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Platform = "AnyCPU"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio 2022 or Build Tools for Visual Studio."
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ([string]::IsNullOrWhiteSpace($vsPath)) {
    throw "MSBuild was not found. Install the Visual Studio MSBuild component."
}

$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "MSBuild.exe was not found at: $msbuild"
}

$desktopBridgeProps = Join-Path $vsPath "MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.props"
if (-not (Test-Path $desktopBridgeProps)) {
    throw "Windows Application Packaging/MSIX targets are missing. In Visual Studio Installer, add the MSIX Packaging Tools and Windows Application Packaging Project support, then run this script again."
}

$appProject = Join-Path $root "AudioConvert\AudioConvert.csproj"
$packageProject = Join-Path $root "AudioConvert.Package\AudioConvert.Package.wapproj"

& $msbuild $appProject /restore /p:Configuration=$Configuration /p:Platform=$Platform /m
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $msbuild $packageProject /p:Configuration=$Configuration /p:Platform=$Platform /p:UapAppxPackageBuildMode=StoreUpload
exit $LASTEXITCODE

