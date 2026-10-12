[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.2',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Install Inno Setup or supply -IsccPath pointing to ISCC.exe.'
}

# Publish into a fresh directory to exclude stale files from previous releases.
$outputDir = Join-Path $repoRoot "reports\installer\$Version-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
$publishDir = Join-Path $outputDir 'publish'
New-Item -ItemType Directory -Path $outputDir | Out-Null

& dotnet publish (Join-Path $repoRoot 'Duplicates\Duplicates.csproj') `
    -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Publishing Duplicates failed (exit $LASTEXITCODE)."
}

foreach ($asset in @('AppIcon.ico', 'Square44x44Logo.targetsize-48.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Assets\$asset"))) {
        throw "Published application is missing Assets\$asset."
    }
}

& $IsccPath "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DInstallerOutputDir=$outputDir" `
    (Join-Path $repoRoot 'installer\Duplicates.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Building the installer failed (exit $LASTEXITCODE)."
}

$installer = Join-Path $outputDir "Duplicates-$Version-Setup-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw 'The installer compiler did not produce the expected file.'
}
Write-Output "Installer: $installer"
