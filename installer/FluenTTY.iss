; FluenTTY installer (Inno Setup 6), same tool and layout as FluentPlay's. Built by tools\build-installer.ps1.
; Per-user install by default (no admin prompt; the wizard offers "install for all users");
; self-contained .NET, so the target PC needs nothing else. Windows lists the app in Start/Search,
; Settings > Apps and Control Panel > Programs and Features through Inno's standard uninstall entry.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\FluenTTY\bin\publish"
#endif

#define AppName "FluenTTY"
#define AppExe "FluenTTY.exe"

[Setup]
; Never change the AppId: upgrades find the existing install through it.
AppId={{6E0D2A57-3C4B-4E8F-9A61-2B7D5F1C8E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=FluenTTY
AppComments=Tabbed SSH, serial, Telnet, SFTP/FTP, RDP and VNC client
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
PrivilegesRequired=lowest
; Inno's own install-mode dialog ("for me only" / "for all users"). Choosing "all users" makes Setup
; start an elevated copy and wait for it, so the dialog stays on screen until that install ends.
; A custom relaunch from [Code] is not possible: on Inno Setup 6.7 Setup's own process gets
; ERROR_ACCESS_DENIED (5) starting {srcexe} with both ShellExec and Exec (tested 2026-09-25).
PrivilegesRequiredOverridesAllowed=dialog
; The install mode is always decided by PrivilegesRequired (or the user in the wizard), never
; inherited from an earlier admin install (see FluentPlay's installer for the reason).
UsePreviousPrivileges=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=FluenTTY-Setup-{#AppVersion}
SetupIconFile=..\FluenTTY\Assets\FluenTTY.ico
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
DisableProgramGroupPage=yes
; Upgrades: Restart Manager closes a running FluenTTY (after the user confirms in the wizard).
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "urlhandlers"; Description: "Open ssh://, telnet:// and sftp:// links with FluenTTY"; GroupDescription: "Links:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\FluenTTY\Licenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "SSH, serial, Telnet, SFTP/FTP, Remote Desktop and VNC"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Optional URL protocol handlers (the app's command line accepts ssh://, telnet://, sftp:// URLs).
#define Url(str Scheme, str Friendly) \
  'Root: HKA; Subkey: "Software\Classes\' + Scheme + '"; ValueType: string; ValueData: "URL:' + Friendly + '"; Flags: uninsdeletekey; Tasks: urlhandlers' + NewLine + \
  'Root: HKA; Subkey: "Software\Classes\' + Scheme + '"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: urlhandlers' + NewLine + \
  'Root: HKA; Subkey: "Software\Classes\' + Scheme + '\DefaultIcon"; ValueType: string; ValueData: "{app}\' + AppExe + ',0"; Tasks: urlhandlers' + NewLine + \
  'Root: HKA; Subkey: "Software\Classes\' + Scheme + '\shell\open\command"; ValueType: string; ValueData: """{app}\' + AppExe + '"" ""%1"""; Tasks: urlhandlers'

#emit Url("ssh", "Secure Shell")
#emit Url("telnet", "Telnet")
#emit Url("sftp", "SFTP")

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; In-app updates (Services\Updater.cs) run this setup with /SILENT /FLUENTTYUPDATE=1: reopen FluenTTY afterwards,
; as the signed-in user even when an all-users update ran elevated.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsInAppUpdate

[Code]
// The app holds this mutex while it runs (Services\SingleInstance.cs; the portable build uses a different name).
const
  AppMutexName = 'Local\FluenTTY.Instance';

function IsInAppUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:FLUENTTYUPDATE|0}') = '1';
end;

// Started by FluenTTY itself: give it time to save and exit before files are replaced. User data
// (%AppData%\FluenTTY) is never part of an upgrade; only the program folder changes.
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsInAppUpdate() then
  begin
    Waited := 0;
    while CheckForMutexes(AppMutexName) and (Waited < 30000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
end;

// A running FluenTTY keeps its files locked; ask the user to close it (it may have live sessions).
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes(AppMutexName) do
  begin
    if MsgBox('FluenTTY is still running.' + #13#10#13#10 +
              'Close it (this ends any open sessions), then click Retry.', mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Settings, saved sessions and known host keys live in %AppData%\FluenTTY. Keep them unless the
    // user says otherwise; a silent uninstall never deletes user data.
    if not UninstallSilent then
      if DirExists(ExpandConstant('{userappdata}\{#AppName}')) then
        if MsgBox('Also delete your FluenTTY settings, saved sessions and known host keys?' + #13#10#13#10 +
                  ExpandConstant('{userappdata}\{#AppName}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
          DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
  end;
end;
