#ifndef AppVersion
  #error Compile through OsmoOverlay.Build, which passes AppVersion, FileVersion, Architecture, SourceDir, RepoRoot, OutputDir and OutputName.
#endif

#define AppGuid "285212C3-78F8-4A92-AE19-52D33596D266"
#define AppMutexName "Global\OsmoOverlay-" + AppGuid
#define AppExeName "OsmoOverlay.exe"

[Setup]
AppId={{{#AppGuid}}
AppMutex={#AppMutexName}
AppName=OsmoOverlay
AppVersion={#AppVersion}
VersionInfoVersion={#FileVersion}
VersionInfoDescription=OsmoOverlay Setup
AppPublisher=Sefinek
AppPublisherURL=https://sefinek.net
AppSupportURL=https://github.com/sefinek/osmo-overlay/issues
AppUpdatesURL=https://github.com/sefinek/osmo-overlay/releases
AppCopyright=Copyright (C) 2026 Sefinek
AppContact=contact@sefinek.net
DefaultDirName={autopf}\OsmoOverlay
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
LicenseFile={#RepoRoot}\LICENSE
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
SetupArchitecture=x64
PrivilegesRequired=lowest
DisableDirPage=no
DirExistsWarning=no
DisableProgramGroupPage=yes
AlwaysShowDirOnReadyPage=yes
ShowLanguageDialog=auto
CloseApplications=yes
Compression=lzma2/max
SolidCompression=yes
WizardStyle=classic dark

SetupIconFile={#RepoRoot}\OsmoOverlay.Gui\Assets\OsmoOverlay.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName=OsmoOverlay

#call EmitLanguagesSection

[CustomMessages]
english.CreateStartMenuIcon=Create a &Start Menu shortcut
arabic.CreateStartMenuIcon=إنشاء اختصار في قائمة &ابدأ
armenian.CreateStartMenuIcon=Ստեղծել &Մեկնարկ ընտրացանկի դյուրանցում
brazilianportuguese.CreateStartMenuIcon=Criar um atalho no menu &Iniciar
bulgarian.CreateStartMenuIcon=Създаване на пряк път в менюто &Старт
catalan.CreateStartMenuIcon=Crea una drecera al menú &Inici
chinesesimplified.CreateStartMenuIcon=创建开始菜单快捷方式(&S)
chinesetraditional.CreateStartMenuIcon=建立開始功能表捷徑(&S)
corsican.CreateStartMenuIcon=Creà un culligamentu in u &menu Start
czech.CreateStartMenuIcon=Vytvořit zástupce v nabídce &Start
danish.CreateStartMenuIcon=Opret en genvej i &Startmenuen
dutch.CreateStartMenuIcon=Snelkoppeling aanmaken in het &Startmenu
finnish.CreateStartMenuIcon=Luo pikakuvake &Käynnistä-valikkoon
french.CreateStartMenuIcon=Créer un raccourci dans le menu &Démarrer
german.CreateStartMenuIcon=Verknüpfung im &Startmenü erstellen
hebrew.CreateStartMenuIcon=צור קיצור דרך ב&תפריט התחלה
hungarian.CreateStartMenuIcon=Parancsikon létrehozása a &Start menüben
italian.CreateStartMenuIcon=Crea un collegamento nel menu &Start
japanese.CreateStartMenuIcon=スタートメニューにショートカットを作成する(&S)
korean.CreateStartMenuIcon=시작 메뉴에 바로 가기 만들기(&S)
lithuanian.CreateStartMenuIcon=Sukurti nuorodą meniu &Pradėti
norwegian.CreateStartMenuIcon=Opprett snarvei i &Start-menyen
polish.CreateStartMenuIcon=Utwórz skrót w &menu Start
portuguese.CreateStartMenuIcon=Criar um atalho no menu &Iniciar
russian.CreateStartMenuIcon=Создать ярлык в меню &Пуск
slovak.CreateStartMenuIcon=Vytvoriť skratku v ponuke &Štart
slovenian.CreateStartMenuIcon=Ustvari bližnjico v meniju &Start
spanish.CreateStartMenuIcon=Crear un acceso directo en el menú &Inicio
swedish.CreateStartMenuIcon=Skapa en genväg i &Start-menyn
tamil.CreateStartMenuIcon=தொடக்க மெனுவில் குறுக்குவழி உருவாக்கு(&S)
thai.CreateStartMenuIcon=สร้างทางลัดใน&เมนูเริ่ม
turkish.CreateStartMenuIcon=&Başlat menüsüne kısayol oluştur
ukrainian.CreateStartMenuIcon=Створити ярлик у меню &Пуск
AppRunningOnUninstall=OsmoOverlay is currently running.%n%nWould you like to close it?
AppCloseFailed=OsmoOverlay couldn't be closed. Please close it manually and try again.
polish.AppRunningOnUninstall=OsmoOverlay jest obecnie uruchomiony.%n%nCzy chcesz go zamknąć?
polish.AppCloseFailed=Nie udało się zamknąć OsmoOverlay. Zamknij go ręcznie i spróbuj ponownie.

[Tasks]
Name: "CreateDesktopIcon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Check: not IsUpdating
Name: "CreateStartMenuIcon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Check: not IsUpdating

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Desktop
Name: "{autodesktop}\OsmoOverlay"; Filename: "{app}\{#AppExeName}"; Check: KeepDesktopShortcut
Name: "{autodesktop}\OsmoOverlay"; Filename: "{app}\{#AppExeName}"; Tasks: CreateDesktopIcon; Check: not IsUpdating

; Start Menu
Name: "{autoprograms}\OsmoOverlay"; Filename: "{app}\{#AppExeName}"; Check: KeepStartMenuShortcut
Name: "{autoprograms}\OsmoOverlay"; Filename: "{app}\{#AppExeName}"; Tasks: CreateStartMenuIcon; Check: not IsUpdating

[Run]
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,OsmoOverlay}"; Flags: nowait postinstall skipifsilent; Check: not IsUpdating
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Flags: nowait postinstall; Check: IsUpdating

[Code]
var
  // Taken before the previous version's uninstaller runs - it deletes the shortcuts unconditionally.
  HadDesktopShortcut, HadStartMenuShortcut: Boolean;

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

function KeepStartMenuShortcut(): Boolean;
begin
  Result := IsUpdating() and HadStartMenuShortcut;
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
  HadStartMenuShortcut := FileExists(ExpandConstant('{autoprograms}\OsmoOverlay.lnk'));
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#AppMutexName}') then Exit;

  if SuppressibleMsgBox(CustomMessage('AppRunningOnUninstall'), mbConfirmation, MB_YESNO, IDYES) <> IDYES then
  begin
    Result := False;
    Exit;
  end;

  Exec('taskkill.exe', '/f /im {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(CustomMessage('AppCloseFailed'), mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  Sleep(1500);
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
