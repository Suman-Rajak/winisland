<#
.SYNOPSIS
    Builds Glyde as an MSIX package and installs it for you, so it has the package identity Windows
    requires before it shares notifications.

.DESCRIPTION
    The package isn't signed, so Windows only accepts it with Developer Mode on
    (Settings > System > For developers > Developer Mode). It's installed in place from the build
    folder ("registered"), which also makes re-running this script an in-place update.

    The installed app keeps its own settings, shelf, timers, speech models and log (in its package
    folder). The first install brings them over: from the app's former package (WinIsland, which it then
    removes) or from the plain build, and moves "Start with Windows" over too.

    Afterwards, start Glyde from the Start menu. To uninstall:
        Get-AppxPackage Glyde | Remove-AppxPackage
#>
$ErrorActionPreference = "Stop"
$name = "Glyde"
$formerName = "WinIsland" # the app's name before; its install and data are carried over once
$project = Join-Path $PSScriptRoot "src\WinIsland\WinIsland.csproj"

$unlock = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue
if ($unlock.AllowDevelopmentWithoutDevLicense -ne 1) {
    Write-Host "Developer Mode is off. Turn it on in Settings > System > For developers, then run this again." -ForegroundColor Yellow
    exit 1
}

# A running copy (packaged or not, either name) would hold its files open.
Get-Process $name, $formerName -ErrorAction SilentlyContinue | Stop-Process
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
    $existing = Get-AppxPackage -Name $name
    if (-not $existing) { throw }
    Write-Host "The package manifest changed; reinstalling (your $name data is kept)."
    Remove-AppxPackage -Package $existing.PackageFullName -PreserveApplicationData
    Add-AppxPackage -Register $manifest -ForceApplicationShutdown
}

$package = Get-AppxPackage -Name $name
$packageData = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState"
New-Item -ItemType Directory -Force $packageData | Out-Null

# The app's former package: move everything it kept (settings, shelf, timers, log, the speech models) into
# the new one, carry "Start with Windows" over if it was on, then remove it.
$former = Get-AppxPackage -Name $formerName
if ($former) {
    # Anything the new package doesn't have yet, folder by folder (removing the old package deletes the rest).
    $formerData = Join-Path $env:LOCALAPPDATA "Packages\$($former.PackageFamilyName)\LocalState"
    if (Test-Path $formerData) {
        foreach ($item in Get-ChildItem $formerData) {
            $target = Join-Path $packageData $item.Name
            if (-not (Test-Path $target)) {
                Move-Item $item.FullName $packageData
            }
            elseif ($item.PSIsContainer) {
                Get-ChildItem $item.FullName | Where-Object { -not (Test-Path (Join-Path $target $_.Name)) } | Move-Item -Destination $target
            }
        }
        Write-Host "Moved your settings, shelf, timers and speech models over from $formerName."
    }

    # Startup task state 2 = enabled (Windows keeps it per package).
    $taskState = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($former.PackageFamilyName)\WinIslandStartup"
    if ((Get-ItemProperty $taskState -Name State -ErrorAction SilentlyContinue).State -eq 2) {
        New-Item -ItemType File -Force (Join-Path $packageData "enable-startup") | Out-Null
        Write-Host "Carried 'Start with Windows' over."
    }

    Remove-AppxPackage -Package $former.PackageFullName
    Write-Host "Removed the old $formerName install."
}

# The plain build's data (it moved itself to %LOCALAPPDATA%\Glyde, or is still under the former name):
# the first time, bring its settings and shelf over so nothing resets.
$plainData = @((Join-Path $env:LOCALAPPDATA $name), (Join-Path $env:LOCALAPPDATA $formerName)) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($plainData) {
    foreach ($file in "settings.json", "shelf.json", "timers.json") {
        if ((Test-Path (Join-Path $plainData $file)) -and -not (Test-Path (Join-Path $packageData $file))) {
            Copy-Item (Join-Path $plainData $file) $packageData
            Write-Host "Copied your $file from the plain build."
        }
    }

    # Speech models are big (465 MB and 574 MB): move the plain build's rather than download them again.
    $packageModels = Join-Path $packageData "Models"
    foreach ($model in Get-ChildItem (Join-Path $plainData "Models") -Filter "*.bin" -ErrorAction SilentlyContinue) {
        if (-not (Test-Path (Join-Path $packageModels $model.Name))) {
            New-Item -ItemType Directory -Force $packageModels | Out-Null
            Move-Item $model.FullName $packageModels
            Write-Host "Moved the speech model $($model.Name) from the plain build."
        }
    }
}

# The plain build's "Start with Windows" entry (under either name) would launch the plain copy at sign-in.
# Swap it for a note that tells the installed app to turn on its own startup task (it can't touch the
# real Run key itself).
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
foreach ($entry in $name, $formerName) {
    if (Get-ItemProperty $runKey -Name $entry -ErrorAction SilentlyContinue) {
        Remove-ItemProperty $runKey -Name $entry
        New-Item -ItemType File -Force (Join-Path $packageData "enable-startup") | Out-Null
        Write-Host "Moved 'Start with Windows' over to the installed app."
    }
}

Write-Host "Installed $name $($package.Version). Starting it..." -ForegroundColor Green
Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
