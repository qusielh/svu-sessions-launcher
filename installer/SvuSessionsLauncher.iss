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
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=wizard-small.bmp
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the [name] Setup Wizard
WelcomeLabel2=This will install [name/ver] for your Windows account.%n%nA student-only client for SVU online sessions. No administrator rights are needed.%n%nClose any running copy of the launcher before continuing, then click Next.

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

procedure InitializeWizard;
var
  BeforePage: TWizardPage;
  Intro, ReqTitle, ReqBody, CheckNote, BetaNote: TNewStaticText;
begin
  BeforePage := CreateCustomPage(wpWelcome, 'Before you install',
    'A quick check before setup copies any files.');

  Intro := TNewStaticText.Create(BeforePage);
  Intro.Parent := BeforePage.Surface;
  Intro.Left := 0;
  Intro.Top := 0;
  Intro.AutoSize := False;
  Intro.WordWrap := True;
  Intro.Width := BeforePage.SurfaceWidth;
  Intro.Caption := 'SVU Sessions Launcher {#AppVersion} installs for your Windows account. ' +
    'It does not need administrator rights.';
  Intro.AdjustHeight;

  ReqTitle := TNewStaticText.Create(BeforePage);
  ReqTitle.Parent := BeforePage.Surface;
  ReqTitle.Left := 0;
  ReqTitle.Top := Intro.Top + Intro.Height + ScaleY(14);
  ReqTitle.Caption := 'Requirements';
  ReqTitle.Font.Style := [fsBold];

  ReqBody := TNewStaticText.Create(BeforePage);
  ReqBody.Parent := BeforePage.Surface;
  ReqBody.Left := 0;
  ReqBody.Top := ReqTitle.Top + ReqTitle.Height + ScaleY(5);
  ReqBody.AutoSize := False;
  ReqBody.WordWrap := True;
  ReqBody.Width := BeforePage.SurfaceWidth;
  ReqBody.Caption := 'Windows 10 or 11 (64-bit), the .NET 10 Desktop Runtime (x64), and Microsoft Edge WebView2. ' +
    'Tutor voice also needs the .NET 10 Runtime (x86) and the TrueSpeech codec.';
  ReqBody.AdjustHeight;

  CheckNote := TNewStaticText.Create(BeforePage);
  CheckNote.Parent := BeforePage.Surface;
  CheckNote.Left := 0;
  CheckNote.Top := ReqBody.Top + ReqBody.Height + ScaleY(12);
  CheckNote.AutoSize := False;
  CheckNote.WordWrap := True;
  CheckNote.Width := BeforePage.SurfaceWidth;
  CheckNote.Caption := 'Setup checks the required runtimes before copying files. If one is missing, ' +
    'it offers the download page and stops; it never installs runtimes silently.';
  CheckNote.AdjustHeight;

  BetaNote := TNewStaticText.Create(BeforePage);
  BetaNote.Parent := BeforePage.Surface;
  BetaNote.Left := 0;
  BetaNote.Top := CheckNote.Top + CheckNote.Height + ScaleY(12);
  BetaNote.AutoSize := False;
  BetaNote.WordWrap := True;
  BetaNote.Width := BeforePage.SurfaceWidth;
  BetaNote.Caption := 'Student accounts only. Tutor and host accounts are blocked in this beta. ' +
    'The app is not code-signed, so Windows may warn on first launch.';
  BetaNote.AdjustHeight;
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
