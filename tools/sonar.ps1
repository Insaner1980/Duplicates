#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$PlanOnly,
    [switch]$AllowExternalUpload,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$SonarArgs
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$OutputEncoding = [Console]::OutputEncoding

function Invoke-DotNetCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$ReportPath
    )

    & dotnet @Arguments 2>&1 |
        Tee-Object -FilePath $ReportPath -Append |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet-komento epäonnistui (exit $LASTEXITCODE)."
    }
}

if ($PlanOnly) {
    Write-Output @(
        'sonar'
        '  - dotnet restore + Debug/x64 build + both xUnit test projects with OpenCover coverage + SonarQube Cloud upload'
        '  - requires SONAR_TOKEN (the local sonar profile loads the saved CLI credential)'
        '  - direct script invocation requires -AllowExternalUpload'
        '  - project: Insaner1980_Duplicates'
        '  - organization: insaner1980'
        '  - host: https://sonarcloud.io'
    )
    exit 0
}

if ($SonarArgs.Count -gt 0) {
    $cli = Get-Command sonar.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $cli) {
        throw 'sonar.exe ei löytynyt PATHista.'
    }

    & $cli.Source @SonarArgs
    exit $LASTEXITCODE
}

if (-not $AllowExternalUpload) {
    throw 'Sonar-analyysi lähettää analyysituloksen SonarQube Cloudiin. Käytä -AllowExternalUpload.'
}

if ([string]::IsNullOrWhiteSpace($env:SONAR_TOKEN)) {
    throw 'SONAR_TOKEN ei ole asetettu tälle PowerShell-istunnolle.'
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$reportsDir = Join-Path $projectRoot 'reports'
$reportPath = Join-Path $reportsDir 'sonar.txt'
$testResultsDir = Join-Path $reportsDir 'sonar-test-results'
$projectKey = 'Insaner1980_Duplicates'
$organization = 'insaner1980'
$projectUrl = "https://sonarcloud.io/project/overview?id=$projectKey"

New-Item -ItemType Directory -Force -Path $reportsDir | Out-Null
if ((Split-Path -Parent ([System.IO.Path]::GetFullPath($testResultsDir))) -ne $reportsDir) {
    throw 'Sonar-testitulosten polku ei ole reports-kansion alla.'
}
if (Test-Path -LiteralPath $testResultsDir) {
    Remove-Item -LiteralPath $testResultsDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
Set-Content -LiteralPath $reportPath -Encoding utf8 -Value @(
    'sonar'
    "Root: $projectRoot"
    "Project: $projectKey"
    "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    ''
)

Push-Location -LiteralPath $projectRoot
try {
    Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
        'tool', 'restore'
    )
    Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
        'restore', 'Duplicates.slnx', '-p:Platform=x64'
    )
    Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
        'tool', 'run', 'dotnet-sonarscanner', '--', 'begin'
        "/k:$projectKey"
        "/o:$organization"
        '/d:sonar.host.url=https://sonarcloud.io'
        "/d:sonar.token=$env:SONAR_TOKEN"
        '/d:sonar.cs.vstest.reportsPaths=reports/sonar-test-results/**/*.trx'
        '/d:sonar.cs.opencover.reportsPaths=reports/sonar-test-results/**/coverage.opencover.xml'
        '/d:sonar.exclusions=**/bin/**,**/obj/**,TestResults/**,reports/**,Duplicates/AppPackages/**,Duplicates/Assets/**'
    )
    Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
        'build', 'Duplicates.slnx', '-c', 'Debug', '-p:Platform=x64'
        '-p:DisableWindowsAppSdkAutoInitialize=true'
        '--no-restore', '--no-incremental', '-m:1'
    )
    foreach ($testProject in @('Duplicates.Engine.Tests', 'Duplicates.App.Tests')) {
        $resultsDir = Join-Path $testResultsDir $testProject
        Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
            'test', "$testProject\$testProject.csproj"
            '-c', 'Debug', '-p:Platform=x64'
            '--no-build', '--no-restore'
            '--results-directory', $resultsDir
            '--logger', 'trx;LogFileName=sonar.trx'
            '--collect', 'XPlat Code Coverage;Format=opencover'
        )
        $coverageReports = @(Get-ChildItem -LiteralPath $resultsDir -Recurse -Filter 'coverage.opencover.xml' -File)
        if ($coverageReports.Count -eq 0) {
            throw "Kattavuusraportti puuttuu: $testProject. Analyysia ei lähetetty."
        }
    }
    Invoke-DotNetCommand -ReportPath $reportPath -Arguments @(
        'tool', 'run', 'dotnet-sonarscanner', '--', 'end'
        "/d:sonar.token=$env:SONAR_TOKEN"
    )
}
finally {
    Pop-Location
}

Write-Output "Tulokset: $projectUrl"
