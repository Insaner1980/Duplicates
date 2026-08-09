# Duplicates

Duplicates is a native Windows 11 WinUI 3 desktop app for finding byte-identical duplicate files and deleting selected copies safely. Detection is content-based, so it works for any file type; extension filters only narrow the scan.

## Stack

- .NET SDK 10.0.301
- Windows App SDK 2.3.1
- WinUI 3, packaged MSIX by default
- `Duplicates.Engine` is a UI-free `net10.0` library
- `Duplicates` is the WinUI app
- `Duplicates.Engine.Tests` contains xUnit tests for the scanner
- `Duplicates.App.Tests` contains xUnit tests for app ViewModels

Package versions are centralized in `Directory.Packages.props`.

## Restore, Build, Test

```powershell
dotnet restore Duplicates.slnx
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

Run from Visual Studio for the packaged debugging path, or try:

```powershell
dotnet run --project Duplicates\Duplicates.csproj -c Debug -p:Platform=x64
```

## Packaging

The preferred distribution path is packaged MSIX. For local sideloading, create and trust a self-signed certificate:

```powershell
.\tools\New-SigningCertificate.ps1
Import-Certificate -FilePath .\certs\Duplicates.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
```

Then build/package from Visual Studio or MSBuild's MSIX targets.

Packaged and framework-dependent debug launches also require Windows App Runtime 2.3 for the current user. Install it with the official 2.3.1 x64 installer:

```powershell
.\tools\Install-WindowsAppRuntime2.3.ps1
```

If certificate friction is not worth it for personal use, publish an unpackaged x64 folder. Framework-dependent unpackaged output still needs the Windows App Runtime above:

```powershell
dotnet publish Duplicates\Duplicates.csproj -c Release -r win-x64 --self-contained -p:WindowsPackageType=None
```

## Safety Defaults

- Byte-by-byte verification is on by default before duplicate groups are shown.
- Delete sends files to the Recycle Bin by default.
- The Results ViewModel prevents selecting every copy in a duplicate group and rechecks that invariant immediately before deletion.
- Search and sort do not reset selected files; Results keeps canonical groups separate from the visible filtered list.
- Delete progress and per-file failures are shown after deletion, and skipped scan paths are surfaced in Results.
- Scan folders and results are not persisted; only settings are saved under `%LOCALAPPDATA%\Duplicates\settings.json`.
