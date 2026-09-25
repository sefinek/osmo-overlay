#ifndef AppVersion
  #error Compile through OsmoOverlay.Build, which passes AppVersion, FileVersion, Architecture, SourceDir, RepoRoot, OutputDir and OutputName.
#endif

#define AppGuid "285212C3-78F8-4A92-AE19-52D33596D266"
#define AppMutexName "OsmoOverlay-" + AppGuid

[Setup]
AppId={{{#AppGuid}}
AppMutex={#AppMutexName}
AppName=OsmoOverlay
AppVersion={#AppVersion}
AppPublisher=Sefinek
AppPublisherURL=https://github.com/sefinek/osmo-overlay
AppSupportURL=https://github.com/sefinek/osmo-overlay/issues
AppUpdatesURL=https://github.com/sefinek/osmo-overlay/releases
AppCopyright=Copyright (C) 2026 Sefinek
VersionInfoVersion={#FileVersion}
VersionInfoDescription=OsmoOverlay Setup

DefaultDirName={autopf}\OsmoOverlay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes

SetupArchitecture=x64
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}

LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\OsmoOverlay.Gui\Assets\OsmoOverlay.ico
UninstallDisplayIcon={app}\OsmoOverlay.exe
UninstallDisplayName=OsmoOverlay
WizardStyle=modern dynamic windows11

OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: not IsUpdating

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OsmoOverlay"; Filename: "{app}\OsmoOverlay.exe"
Name: "{autodesktop}\OsmoOverlay"; Filename: "{app}\OsmoOverlay.exe"; Tasks: desktopicon; Check: not IsUpdating
Name: "{autodesktop}\OsmoOverlay"; Filename: "{app}\OsmoOverlay.exe"; Check: KeepDesktopShortcut

[Run]
Filename: "{app}\OsmoOverlay.exe"; Description: "{cm:LaunchProgram,OsmoOverlay}"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: not IsUpdating
Filename: "{app}\OsmoOverlay.exe"; Flags: nowait postinstall runascurrentuser; Check: IsUpdating

[Code]
var
  // Taken before the previous version's uninstaller runs - it deletes the shortcut unconditionally.
  HadDesktopShortcut: Boolean;

function IsUpdating(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/UPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function KeepDesktopShortcut(): Boolean;
begin
  Result := IsUpdating() and HadDesktopShortcut;
end;

function GetUninstallString(): String;
var
  Key: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  Result := '';
  if not RegQueryStringValue(HKCU, Key, 'UninstallString', Result) then
    RegQueryStringValue(HKLM, Key, 'UninstallString', Result);
end;

function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  // The app starts the update and exits right away - give it a moment to close before AppMutex is checked.
  Waited := 0;
  while IsUpdating() and CheckForMutexes('{#AppMutexName}') and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  HadDesktopShortcut := FileExists(ExpandConstant('{autodesktop}\OsmoOverlay.lnk'));
  Result := True;
end;

// Removes the previous version first, so files it had and this one doesn't (renamed or dropped DLLs) don't stay behind.
procedure UninstallPreviousVersion();
var
  UninstallString: String;
  ResultCode: Integer;
begin
  UninstallString := RemoveQuotes(GetUninstallString());
  if UninstallString = '' then Exit;

  if not Exec(UninstallString, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('UninstallPreviousVersion: failed to start "%s"', [UninstallString]))
  else if ResultCode <> 0 then
    Log(Format('UninstallPreviousVersion: "%s" exited with code %d', [UninstallString, ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then UninstallPreviousVersion();
end;
