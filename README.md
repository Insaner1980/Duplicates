# Duplicates

Duplicates is a native Windows 11 WinUI 3 desktop app with 14 local file-analysis and cleanup tools. It finds byte-identical duplicates, similar images/videos, metadata-matched music, empty/big/temporary files and folders, invalid links, broken files, bad extensions/names, and provides transactional EXIF removal and video optimization.

Exact duplicates are content-verified and survivor-protected. Similar-media matches are review candidates and are never cleaned automatically.

## Stack

- .NET SDK 10.0.301
- Windows App SDK 2.3.1
- WinUI 3, packaged MSIX by default
- `Duplicates.Engine` is a UI-free `net10.0` exact-scanner and analysis library
- `Duplicates` is the WinUI app
- `Duplicates.Engine.Tests` contains xUnit tests for scanning, inventory, analyzers, and media algorithms
- `Duplicates.App.Tests` contains xUnit tests for app services/ViewModels, native XAML contracts, and Windows-provider seams

Package versions are centralized in `Directory.Packages.props`.

## Restore, Build, Test

```powershell
dotnet restore Duplicates.slnx
dotnet test Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj -c Debug --no-restore
dotnet test Duplicates.App.Tests\Duplicates.App.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore
```

For an unpackaged Debug x64 launch, build explicitly without MSIX and run the direct executable:

```powershell
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 --no-restore -p:WindowsPackageType=None
& .\Duplicates\bin\x64\Debug\net10.0-windows10.0.22621.0\Duplicates.exe
```

Task 20 verified fresh unpackaged and packaged Debug x64 identities and broad native flows. Final isolated 100,000-file scans reached responsive Results in 94.035 s unpackaged and 63.567 s packaged, with virtualization and End/Home keyboard traversal verified in both modes. These are one-host NTFS/NVMe measurements, not cross-machine guarantees.

The historical Task 20 GUI verification used Windows App SDK 1.8. The merged Windows App SDK 2.3 build requires a separate packaged/unpackaged GUI acceptance run.

For the packaged Debug x64 development launch, generate a fresh MSIX, unpack it to a unique loose layout with the x64 Windows SDK MakeAppx, remove only the generated root block map, and register the manifest:

```powershell
dotnet build Duplicates\Duplicates.csproj -c Debug -p:Platform=x64 -t:Rebuild --no-restore
dotnet msbuild Duplicates\Duplicates.csproj -t:_GenerateAppxPackage -p:Configuration=Debug -p:Platform=x64 -p:AppxBundle=Never -p:AppxPackageSigningEnabled=false

$msix = (Resolve-Path -LiteralPath '.\Duplicates\AppPackages\Duplicates_1.0.0.0_x64_Debug_Test\Duplicates_1.0.0.0_x64_Debug.msix').Path
$layout = Join-Path (Resolve-Path -LiteralPath '.\reports').Path ('task20-pkg-layout-' + [guid]::NewGuid().ToString('D'))
[void](New-Item -ItemType Directory -Path $layout)
$makeAppx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
& $makeAppx unpack /p $msix /d $layout /o
if ($LASTEXITCODE -ne 0) { throw "makeappx exit $LASTEXITCODE" }

$blockMap = Join-Path $layout 'AppxBlockMap.xml'
[IO.File]::Delete($blockMap)
if (Test-Path -LiteralPath $blockMap) { throw 'AppxBlockMap.xml deletion failed' }

$manifest = Join-Path $layout 'AppxManifest.xml'
Add-AppxPackage -Register $manifest -ErrorAction Stop
$package = Get-AppxPackage -Name 'D8D23102-5301-46E2-A506-C486B1E205FB'
if ($package.Status -ne 'Ok' -or -not $package.IsDevelopmentMode) { throw 'Development package registration failed' }
if ((Resolve-Path -LiteralPath $package.InstallLocation).Path -ne (Resolve-Path -LiteralPath $layout).Path) { throw 'Unexpected package install location' }
Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!App"
```

The successful Task 20 host used x64 MakeAppx `10.0.26100.8249`; another Windows SDK may install the executable under a different version directory. After closing the app, remove the development package before deleting its exact layout.

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
- The sole remaining KEEP row has a disabled native delete-selection checkbox; bulk selection rules and Undo publish one totals refresh after their loop.
- Search and sort do not reset hidden selection: exact groups, flat findings, and similarity groups keep canonical state separate from visible projections.
- Move/Delete actions capture the initiating result session, revalidate tool-specific predicates, and ignore service-summary canonical reconciliation and completion status when that session is no longer current.
- Bad extension/name findings are rename-only; EXIF/video output rows are Open/Reveal only; similarity mutations are always manual.
- EXIF replacement and video sibling-publication workflows stage, validate, commit, and clean up or roll back before coordinator completion.
- Skipped paths and per-file/provider failures remain visible.
- Result sessions are transient. Settings are saved under `%LOCALAPPDATA%\Duplicates\settings.json`; optional media fingerprints use `%LOCALAPPDATA%\Duplicates\cache\media-fingerprints-v1.json`.
