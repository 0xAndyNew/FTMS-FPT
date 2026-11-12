#define MyAppName "FTMS Companion"
#define MyAppVersion "1.0.67"
#define MyAppPublisher "FPT Telecom International"
#define MyAppPublisherURL "https://fti.fpt.vn"
#define MyAppSupportURL "https://ftms.fpt.net"
#define MyAppUpdatesURL "https://ftms.fpt.net"
#define MyAppCopyright "Copyright (C) 2026 FPT Telecom International. All rights reserved."
#define MyAppComments "FTMS Companion - Ung dung giam sat ticket FTMS va tuong tac Telegram cho ky thuat vien phong TOC Data Center."
#define MyAppExeName "FTMS.Companion.exe"

[Setup]
AppId={{D39E2419-DC52-4A20-B477-6E713B273A62}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppPublisherURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppUpdatesURL}
AppCopyright={#MyAppCopyright}
AppComments={#MyAppComments}
DefaultDirName={autopf}\FTMS Companion
DefaultGroupName=FTMS Companion
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FTMS-Companion-Setup-{#MyAppVersion}
SetupIconFile=..\picture\ftms-app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup - Bo cai dat FTMS Companion
VersionInfoTextVersion={#MyAppVersion}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoOriginalFileName=FTMS-Companion-Setup-{#MyAppVersion}.exe

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình Desktop"; GroupDescription: "Biểu tượng:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\FTMS Companion"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\FTMS Companion"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Đang cài Microsoft Edge WebView2 Runtime..."; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Khởi chạy FTMS Companion"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
