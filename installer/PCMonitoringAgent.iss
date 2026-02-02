#define AppName "PC Monitoring Agent"
#define AppExeName "Agent.exe"
#define AppVersion "1.0.0"
#define AppPublisher "PC Monitoring"
#define AppUrl "https://localhost"
#define AppPort "8787"

[Setup]
AppId={{7CBE38F4-5C92-4D3A-9A80-2D0A9E7B0E7F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
DefaultDirName={pf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\dist\installer
OutputBaseFilename=PCMonitoringAgentSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autorun"; Description: "Start agent on login"; Flags: unchecked
Name: "firewall"; Description: "Allow LAN access (open firewall port {#AppPort})"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; Tasks: autorun; Flags: uninsdeletevalue

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow protocol=TCP localport={#AppPort}"; Flags: runhidden; Tasks: firewall

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; Tasks: firewall
