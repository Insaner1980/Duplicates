# dotnet format needs the engine output to load the WinUI project.
dotnet build (Join-Path $PSScriptRoot '..\Duplicates.Engine\Duplicates.Engine.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& "C:\Dev\Windows-check\tools\InvokeWindowsProjectCheck.ps1" -Check lint @args
