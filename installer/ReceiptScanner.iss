; Inno Setup 7 script. Build with scripts/build-installer.sh, which passes AppVersion.
; Installs for the current user, so no administrator rights are needed.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif

[Setup]
AppId={{7F3C2A61-9B4E-4D57-8A1C-3E6D0B92F1A4}
AppName=Receipt Scanner
AppVersion={#AppVersion}
AppPublisher=Malverma
AppPublisherURL=https://github.com/Malverma/Doc-Scanner
DefaultDirName={localappdata}\Programs\Receipt Scanner
DefaultGroupName=Receipt Scanner
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=ReceiptScanner-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ReceiptScanner.exe
UninstallDisplayName=Receipt Scanner

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
; Includes the worker subfolder, which must stay beside the app.
Source: "..\dist\ReceiptScanner\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "dotnet-THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Receipt Scanner"; Filename: "{app}\ReceiptScanner.exe"
Name: "{autodesktop}\Receipt Scanner"; Filename: "{app}\ReceiptScanner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ReceiptScanner.exe"; Description: "Open Receipt Scanner"; Flags: nowait postinstall skipifsilent
