; Inno Setup 6 script for the framework-dependent SVU Sessions Launcher.
; Build with .\build-installer.ps1, which supplies the version and stages the app.
; The installer is per-user: no administrator rights, no silent runtime installs.

#ifndef AppVersion
  #define AppVersion "3.0.0-beta.1"
#endif
#ifndef StageDir
  #define StageDir "..\dist\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "SVU Sessions Launcher"
#define AppPublisher "Qusie"
#define AppURL "https://github.com/qusielh/svu-sessions-launcher"
#define AppExeName "SvuSessionsLauncher.exe"

[Setup]
AppId={{139FD741-0EB5-468B-BD78-641B5891DD0E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright (c) 2026 Qusie. MIT licensed.
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion=3.0.0.1
VersionInfoVersion=3.0.0.1
DefaultDirName={localappdata}\Programs\SVU Sessions Launcher
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=SVU-Sessions-Launcher-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\SvuSessionsLauncher\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
InfoBeforeFile=BeforeInstall.txt
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#StageDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function HasDotNetRuntime(const BaseDir, Framework: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(BaseDir + '\' + Framework + '\10.*', FindRec);
  if Result then
    FindClose(FindRec);
end;

function HasWebView2(): Boolean;
var
  Version: string;
begin
  Result := RegQueryStringValue(
    HKCU,
    'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv',
    Version);
  if not Result then
    Result := RegQueryStringValue(
      HKLM32,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv',
      Version);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  VoiceMissing: string;
begin
  Result := True;

  if not HasDotNetRuntime(ExpandConstant('{pf}\dotnet\shared'), 'Microsoft.WindowsDesktop.App') then
  begin
    if MsgBox(
      'SVU Sessions Launcher needs the .NET 10 Desktop Runtime (x64).' + #13#10#13#10 +
      'Open the .NET 10 download page now?',
      mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOW, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  if not HasWebView2() then
  begin
    if MsgBox(
      'SVU Sessions Launcher needs the Microsoft Edge WebView2 Runtime.' + #13#10#13#10 +
      'Open the WebView2 download page now?',
      mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOW, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  VoiceMissing := '';
  if not HasDotNetRuntime(ExpandConstant('{pf32}\dotnet\shared'), 'Microsoft.NETCore.App') then
    VoiceMissing := VoiceMissing + '- .NET 10 Runtime (x86)' + #13#10;
  if not FileExists(ExpandConstant('{win}\SysWOW64\tssoft32.acm')) then
    VoiceMissing := VoiceMissing + '- TrueSpeech ACM codec' + #13#10;

  if VoiceMissing <> '' then
    MsgBox(
      'Tutor voice playback will be unavailable until these are installed:' + #13#10#13#10 +
      VoiceMissing + #13#10 +
      'The rest of the launcher will work normally.',
      mbInformation, MB_OK);
end;
