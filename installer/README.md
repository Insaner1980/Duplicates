# Windows installer

Build a per-user Windows 11 x64 installer with .NET SDK and Inno Setup installed:

```powershell
pwsh -File tools/Build-Installer.ps1 -Version 1.0.1
```

The command prints the setup EXE path under `reports/installer/`. It publishes
Release binaries with both .NET and Windows App SDK included. Installation needs
no administrator rights and creates a Start menu shortcut with the app icon.
A desktop shortcut is optional. Later installers use the same AppId to update
the installed application. Uninstall is available through Windows Settings and
preserves settings and cache under `%LOCALAPPDATA%\Duplicates`.

This installer is unsigned. It does not replace or remove a Visual Studio/MSIX
development registration. Such a registration can appear alongside the installed
app in Windows Search; remove the old development entry separately if necessary.
