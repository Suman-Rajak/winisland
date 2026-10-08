<#
.SYNOPSIS
    Builds WinIsland as an MSIX package and installs it for you, so it has the package identity Windows
    requires before it shares notifications.

.DESCRIPTION
    The package isn't signed, so Windows only accepts it with Developer Mode on
    (Settings > System > For developers > Developer Mode). It's installed in place from the build
    folder ("registered"), which also makes re-running this script an in-place update.

    The installed app keeps its own settings, shelf and log (in its package folder); the first install
    copies the plain build's settings and shelf across and moves "Start with Windows" over.

    Afterwards, start WinIsland from the Start menu. To uninstall:
        Get-AppxPackage WinIsland | Remove-AppxPackage
#>
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\WinIsland\WinIsland.csproj"

$unlock = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
if ($unlock.AllowDevelopmentWithoutDevLicense -ne 1) {
    Write-Host "Developer Mode is off. Turn it on in Settings > System > For developers, then run this again." -ForegroundColor Yellow
    exit 1
}

# A running copy (packaged or not) would hold its files open.
Get-Process WinIsland -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

dotnet build $project -c Packaged -p:Platform=x64 -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$manifest = Join-Path $PSScriptRoot "src\WinIsland\bin\x64\Packaged\net10.0-windows10.0.26100.0\AppxManifest.xml"
try {
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown
}
catch {
    # A changed manifest with the same version can't be re-registered in place. Unregister the old one,
    # keeping its data (settings, shelf, log), and register again.
    $existing = Get-AppxPackage -Name WinIsland
    if (-not $existing) { throw }
    Write-Host "The package manifest changed; reinstalling (your WinIsland data is kept)."
    Remove-AppxPackage -Package $existing.PackageFullName -PreserveApplicationData
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown
}

$package = Get-AppxPackage -Name WinIsland

# The installed app keeps its data in the package's own folder. The first time, bring over the plain
# build's settings and shelf so nothing resets.
$plainData = Join-Path $env:LOCALAPPDATA "WinIsland"
$packageData = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState"
New-Item -ItemType Directory -Force $packageData | Out-Null
foreach ($file in "settings.json", "shelf.json") {
    if ((Test-Path (Join-Path $plainData $file)) -and -not (Test-Path (Join-Path $packageData $file))) {
        Copy-Item (Join-Path $plainData $file) $packageData
        Write-Host "Copied your $file from the plain build."
    }
}

# Siro's speech model is big (465 MB): move the plain build's rather than download it a second time.
$plainModel = Join-Path $plainData "Models\ggml-small.bin"
$packageModels = Join-Path $packageData "Models"
if ((Test-Path $plainModel) -and -not (Test-Path (Join-Path $packageModels "ggml-small.bin"))) {
    New-Item -ItemType Directory -Force $packageModels | Out-Null
    Move-Item $plainModel $packageModels
    Write-Host "Moved the speech model from the plain build."
}

# The plain build's "Start with Windows" entry would launch the old copy at sign-in. Swap it for a note
# that tells the installed app to turn on its own startup task (it can't touch the real Run key itself).
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Get-ItemProperty $runKey -Name WinIsland -ErrorAction SilentlyContinue) {
    Remove-ItemProperty $runKey -Name WinIsland
    New-Item -ItemType File -Force (Join-Path $packageData "enable-startup") | Out-Null
    Write-Host "Moved 'Start with Windows' over to the installed app."
}

Write-Host "Installed WinIsland $($package.Version). Starting it..." -ForegroundColor Green
Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
