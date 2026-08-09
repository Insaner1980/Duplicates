$ErrorActionPreference = "Stop"

$version = "2.3.1"
$installerUrl = "https://aka.ms/windowsappsdk/2.3/$version/windowsappruntimeinstall-x64.exe"
$installerPath = Join-Path $env:TEMP "WindowsAppRuntimeInstall-$version-x64.exe"

Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath

& $installerPath --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Windows App Runtime installer failed with exit code $LASTEXITCODE."
}

Write-Host "Installed Windows App Runtime $version (x64)."
