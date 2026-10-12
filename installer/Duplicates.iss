#ifndef AppVersion
  #error AppVersion must be supplied by tools/Build-Installer.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by tools/Build-Installer.ps1.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by tools/Build-Installer.ps1.
#endif

[Setup]
; Keep this identity unchanged for future upgrades.
AppId={{45C5B164-7521-44CF-8F73-88F422703AA2}
AppName=Duplicates
AppVersion={#AppVersion}
AppPublisher=Finnvek
DefaultDirName={localappdata}\Programs\Duplicates
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Duplicates-{#AppVersion}-Setup-x64
SetupIconFile=..\Duplicates\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Duplicates.exe
VersionInfoVersion={#AppVersion}.0
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "finnish"; MessagesFile: "compiler:Languages\Finnish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Duplicates"; Filename: "{app}\Duplicates.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{autodesktop}\Duplicates"; Filename: "{app}\Duplicates.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\Duplicates.exe"; Description: "{cm:LaunchProgram,Duplicates}"; Flags: nowait postinstall skipifsilent
