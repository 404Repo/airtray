#define AppName "AirTray"
#define AppVersion "1.0.0"
#define AppPublisher "AirTray"
#define AppExeName "AirTray.exe"
[Setup]
AppId={{7A65B859-0957-458E-8AA9-A4BFE050C8A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\AirTray
DefaultGroupName=AirTray
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts
OutputBaseFilename=AirTray-Setup-1.0.0-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\AirTray.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
SetupLogging=yes
[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\runtime\windowsdesktop-runtime-8-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
[Icons]
Name: "{group}\AirTray"; Filename: "{app}\AirTray.exe"
Name: "{group}\AirTray deinstallieren"; Filename: "{uninstallexe}"
[Run]
Filename: "{tmp}\windowsdesktop-runtime-8-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installiere .NET Desktop Runtime 8..."; Flags: waituntilterminated; Check: not HasDesktopRuntime8
Filename: "{app}\AirTray.exe"; Description: "AirTray starten"; Flags: nowait postinstall skipifsilent
[Code]
function HasDesktopRuntime8: Boolean;
var
  Root: string;
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Root := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetSubkeyNames(HKLM64, Root, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Copy(Names[I], 1, 2) = '8.') then
      begin
        Result := True;
        Exit;
      end;
end;