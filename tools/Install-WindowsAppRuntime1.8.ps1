$ErrorActionPreference = "Stop"

$version = "1.8.260529003"
$zipUrl = "https://download.microsoft.com/download/c41701bb-9202-4a1b-b21c-bd61d90dc173/Microsoft.WindowsAppRuntime.Redist.$version.zip"
$zipPath = Join-Path $env:TEMP "Microsoft.WindowsAppRuntime.Redist.$version.zip"
$extractPath = Join-Path $env:TEMP "Microsoft.WindowsAppRuntime.Redist.$version"

Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath

if (Test-Path $extractPath) {
    Remove-Item -LiteralPath $extractPath -Recurse -Force
}

Expand-Archive -Path $zipPath -DestinationPath $extractPath

$packagePath = Join-Path $extractPath "MSIX\win10-x64"
Get-ChildItem -Path $packagePath -Filter "*.msix" |
    Sort-Object Name |
    ForEach-Object {
        try {
            Add-AppxPackage -Path $_.FullName -ForceApplicationShutdown
            Write-Host "Installed $($_.Name)"
        }
        catch {
            if ($_.Exception.Message -like "*higher version*") {
                Write-Host "Skipped $($_.Name): a higher version is already installed."
            }
            else {
                throw
            }
        }
    }
