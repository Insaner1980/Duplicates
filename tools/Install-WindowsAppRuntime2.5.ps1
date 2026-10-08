$ErrorActionPreference = "Stop"

$version = "2.5.1"
$installerUrl = "https://aka.ms/windowsappsdk/2.5/$version/windowsappruntimeinstall-x64.exe"
$installerPath = Join-Path $env:TEMP "WindowsAppRuntimeInstall-$version-x64-$([guid]::NewGuid().ToString('N')).exe"
[IO.File]::Open($installerPath, [IO.FileMode]::CreateNew).Dispose()

try {
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath

    & $installerPath --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Windows App Runtime installer failed with exit code $LASTEXITCODE."
    }
} finally {
    Remove-Item -LiteralPath $installerPath -Force
}

Write-Host "Installed Windows App Runtime $version (x64)."
