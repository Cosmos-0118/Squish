; Clop for Windows installer definition
#define MyAppName "Clop for Windows"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "Cosmos-0118"
#define MyAppExeName "ClopWindows.exe"
#define MyAppAumid "Cosmos0118.Clop"
#ifndef AppBuildOutput
  #define AppBuildOutput "..\\src\\App\\bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\publish"
#endif
#ifndef CliBuildOutput
  #define CliBuildOutput "..\\src\\CliBridge\\bin\\Release\\net8.0\\win-x64\\publish"
#endif
#ifndef ExplorerBuildOutput
  #define ExplorerBuildOutput "..\\src\\Integrations\\Explorer\\bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\publish"
#endif
#ifndef ToolsRoot
  #define ToolsRoot "..\\tools"
#endif

[Setup]
AppId={{C6C6E8C1-6D1B-4A02-92F8-08E2E6D70B3D}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL="https://github.com/Cosmos-0118"
AppSupportURL="https://github.com/Cosmos-0118/Clop-For-Windows/issues"
AppUpdatesURL="https://github.com/Cosmos-0118/Clop-For-Windows/releases"
DefaultDirName={autopf64}\\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=Clop-Setup-{#MyAppVersion}
SetupIconFile=assets\\Clop.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\\{#MyAppExeName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
LicenseFile=CLOP_LICENSE.txt
UninstallDisplayName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription="Clop for Windows installer"

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "runatstartup"; Description: "Start Clop when I sign in"; GroupDescription: "Automation:"; Flags: unchecked
Name: "installcli"; Description: "Register the clop CLI"; GroupDescription: "Automation:"; Flags: checkedonce
Name: "registerexplorer"; Description: "Enable Explorer context menu"; GroupDescription: "Automation:"; Flags: checkedonce

[Dirs]
Name: "{app}\\cli"; Tasks: installcli
Name: "{app}\\integrations"; Tasks: registerexplorer
Name: "{app}\\tools"; Flags: uninsalwaysuninstall

[Files]
Source: "{#AppBuildOutput}\\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#CliBuildOutput}\\ClopWindows.CliBridge.exe"; DestDir: "{app}\\cli"; DestName: "clop.exe"; Flags: ignoreversion
Source: "{#CliBuildOutput}\\*"; DestDir: "{app}\\cli"; Flags: recursesubdirs ignoreversion; Excludes: "ClopWindows.CliBridge.exe"
Source: "{#ExplorerBuildOutput}\\ClopWindows.Integrations.Explorer.dll"; DestDir: "{app}\\integrations"; Flags: ignoreversion
Source: "{#ExplorerBuildOutput}\\ClopWindows.Integrations.Explorer.comhost.dll"; DestDir: "{app}\\integrations"; Flags: ignoreversion
Source: "{#ToolsRoot}\\*"; DestDir: "{app}\\tools"; Flags: recursesubdirs ignoreversion
Source: "..\\docs\\windows-deps.md"; DestDir: "{app}\\docs"; Flags: ignoreversion
Source: "..\\docs\\cli.md"; DestDir: "{app}\\docs"; Flags: ignoreversion
Source: "..\\docs\\automation-samples.md"; DestDir: "{app}\\docs"; Flags: ignoreversion
Source: "CLOP_LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#MyAppAumid}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon; WorkingDir: "{app}"; AppUserModelID: "{#MyAppAumid}"

[Registry]
Root: HKCU; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run"; ValueType: string; ValueName: "Clop"; ValueData: """{app}\\{#MyAppExeName}"""; Tasks: runatstartup
Root: HKCU; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\clop.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\\cli\\clop.exe"; Tasks: installcli
Root: HKCU; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\clop.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}\\cli"; Tasks: installcli

[Run]
Filename: "{sys}\\regsvr32.exe"; Parameters: "/s ""{app}\\integrations\\ClopWindows.Integrations.Explorer.comhost.dll"""; Tasks: registerexplorer; Flags: runhidden
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\\regsvr32.exe"; Parameters: "/u /s ""{app}\\integrations\\ClopWindows.Integrations.Explorer.comhost.dll"""; RunOnceId: "UnregisterExplorer"

[Code]
const
  PrevUninstallKey = 'SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{C6C6E8C1-6D1B-4A02-92F8-08E2E6D70B3D}';

type
  TSystemTime = record
    Year: Word;
    Month: Word;
    DayOfWeek: Word;
    Day: Word;
    Hour: Word;
    Minute: Word;
    Second: Word;
    Millisecond: Word;
  end;

procedure GetLocalTime(var lpSystemTime: TSystemTime);
  external 'GetLocalTime@kernel32.dll stdcall';

function PadTwoDigits(Value: Integer): string;
begin
  if Value < 10 then
    Result := '0' + IntToStr(Value)
  else
    Result := IntToStr(Value);
end;

function BuildTimestamp: string;
var
  ST: TSystemTime;
begin
  GetLocalTime(ST);
  Result := IntToStr(ST.Year) + PadTwoDigits(ST.Month) + PadTwoDigits(ST.Day) + PadTwoDigits(ST.Hour) + PadTwoDigits(ST.Minute) + PadTwoDigits(ST.Second);
end;

function TryGetPreviousUninstallString(var UninstallString: string): Boolean;
begin
  Result := RegQueryStringValue(HKLM64, PrevUninstallKey, 'UninstallString', UninstallString) or
            RegQueryStringValue(HKCU, PrevUninstallKey, 'UninstallString', UninstallString);
end;

function RunUninstallerSilently(const UninstallString: string): Boolean;
var
  ExitCode: Integer;
begin
  Result := False;
  if UninstallString = '' then Exit;
  if Exec(ExpandConstant('{cmd}'), '/C ' + UninstallString + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := ExitCode = 0;
end;

procedure BackupUserData;
var
  SourceDir, TargetDir, Timestamp: string;
  ResultCode: Integer;
begin
  SourceDir := ExpandConstant('{userappdata}\\Clop');
  if not DirExists(SourceDir) then Exit;
  Timestamp := BuildTimestamp;
  TargetDir := ExpandConstant('{tmp}\\Clop_Backup_' + Timestamp);
  if not ForceDirectories(TargetDir) then Exit;
  if not Exec('robocopy', '"' + SourceDir + '" "' + TargetDir + '" /MIR /FFT /Z /NFL /NDL', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exec(ExpandConstant('{cmd}'), '/C xcopy "' + SourceDir + '" "' + TargetDir + '" /E /I /Y /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup: Boolean;
var
  UninstallString: string;
begin
  Result := True;
  if TryGetPreviousUninstallString(UninstallString) then
  begin
    if MsgBox('A previous Clop installation was detected. Remove it before continuing?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      if not RunUninstallerSilently(UninstallString) then
        if MsgBox('Automatic removal failed. Cancel setup?', mbConfirmation, MB_YESNO) = IDYES then
          Result := False;
    end;
  end;
  if Result then BackupUserData;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('Clop installation completed.');
end;
