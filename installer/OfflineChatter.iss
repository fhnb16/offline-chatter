; Установщик Offline Chatter (Inno Setup 6).
; По умолчанию — для текущего пользователя, без прав администратора, в %LOCALAPPDATA%\Programs\OfflineChatter
; (туда же программа предлагает перенести себя при запуске из «Загрузок»). В окне выбора можно поставить для всех
; пользователей в Program Files — тогда данные будут храниться в %LOCALAPPDATA%\OfflineChatter.
; Сборка (CI делает это сам):
;   iscc /DAppVersion=0.1.42 /DStageDir=..\out\stage\OfflineChatter /DOutDir=..\out installer\OfflineChatter.iss
; Вариант со встроенным .NET Framework 4.8 (офлайн-установщик Microsoft, ~120 МБ) — для Windows 7/8.1 и машин без сети:
;   iscc ... /DNet48=путь\к\NDP48-x86-x64-AllOS-ENU.exe installer\OfflineChatter.iss  → OfflineChatter-X.Y.N-Setup-NET48.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\out\stage\OfflineChatter"
#endif
#ifndef OutDir
  #define OutDir "..\out"
#endif

[Setup]
AppId={{6B1E2F0A-3C4D-4E7B-9A51-0F7C2D9E8B14}
AppName=Offline Chatter
AppVersion={#AppVersion}
AppVerName=Offline Chatter {#AppVersion}
AppPublisher=fhnb16
AppPublisherURL=https://github.com/fhnb16/offline-chatter
AppSupportURL=https://github.com/fhnb16/offline-chatter/issues
AppUpdatesURL=https://github.com/fhnb16/offline-chatter/releases
DefaultDirName={autopf}\OfflineChatter
DefaultGroupName=Offline Chatter
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
OutputDir={#OutDir}
#ifdef Net48
OutputBaseFilename=OfflineChatter-{#AppVersion}-Setup-NET48
#else
OutputBaseFilename=OfflineChatter-{#AppVersion}-Setup
#endif
SetupIconFile=..\src\OfflineChatter\Assets\app.ico
UninstallDisplayIcon={app}\OfflineChatter.exe
UninstallDisplayName=Offline Chatter
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Нужна Windows 7 SP1 (.NET Framework 4.8 без SP1 не устанавливается). Проверка — в InitializeSetup:
; там объясняется, что делать, вместо стандартного «требуется более новая версия Windows»
MinVersion=6.1
CloseApplications=yes
RestartApplications=no
; Подпись Authenticode: ISCC запускают с /DSign=1 и /Socsign="команда подписи $f" (см. build.yml).
; Подписываются и установщик, и встроенный деинсталлятор.
#ifdef Sign
SignTool=ocsign
SignedUninstaller=yes
#endif

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.NoNet48=Для работы Offline Chatter нужен .NET Framework 4.8. Он уже есть в Windows 10 (1903+) и Windows 11. Для Windows 7 и 8.1 скачайте установщик программы с .NET (файл ...-Setup-NET48.exe на странице релиза) или сам .NET Framework 4.8 на сайте Microsoft.%n%nПродолжить установку без него?
en.NoNet48=Offline Chatter needs .NET Framework 4.8. It is built into Windows 10 (1903+) and Windows 11. For Windows 7 and 8.1 download the installer with .NET (the ...-Setup-NET48.exe file on the release page) or .NET Framework 4.8 itself from Microsoft.%n%nContinue without it?
ru.NoSp1=Offline Chatter работает в Windows 7 только с пакетом обновления 1 (SP1): без него не устанавливается .NET Framework 4.8.%n%nУстановите SP1 (обновление KB976932, есть в каталоге Центра обновления Майкрософт и через Центр обновления Windows), перезагрузите компьютер и запустите этот установщик снова.%n%nОткрыть страницу загрузки SP1?
en.NoSp1=Offline Chatter needs Service Pack 1 on Windows 7: .NET Framework 4.8 cannot be installed without it.%n%nInstall SP1 (update KB976932, available in the Microsoft Update Catalog and via Windows Update), restart the computer and run this installer again.%n%nOpen the SP1 download page?
ru.InstallNet48=Будет установлен .NET Framework 4.8 — без него программа не запустится. Нужны права администратора (Windows спросит разрешение), установка займёт несколько минут, после неё может понадобиться перезагрузка.
en.InstallNet48=.NET Framework 4.8 will be installed — the program cannot run without it. Administrator rights are needed (Windows will ask for permission); it takes a few minutes and may require a restart.
ru.InstallRootCert=%n%nВ этой Windows 7 нет корневого сертификата Microsoft Root Certificate Authority 2011, которым подписан установщик .NET (так бывает без обновлений или без сети). Он будет добавлен в хранилище доверенных корневых сертификатов компьютера — так советует сама Microsoft.
en.InstallRootCert=%n%nThis Windows 7 lacks the Microsoft Root Certificate Authority 2011 certificate that signs the .NET installer (common without updates or network access). It will be added to the computer's trusted root store, as Microsoft recommends.
ru.ContinueQuestion=%n%nПродолжить?
en.ContinueQuestion=%n%nContinue?
ru.Net48Failed=.NET Framework 4.8 установить не удалось. Программа будет установлена, но запустится только после установки .NET Framework 4.8 (сайт Microsoft, «.NET Framework 4.8 offline installer»).
en.Net48Failed=.NET Framework 4.8 could not be installed. The program will be installed but will only start after .NET Framework 4.8 is installed (Microsoft website, ".NET Framework 4.8 offline installer").

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef Net48
; Распаковываются во временную папку только при необходимости (ExtractTemporaryFile в PrepareToInstall)
Source: "{#Net48}"; DestName: "ndp48.exe"; Flags: dontcopy
Source: "certs\MicRooCerAut2011.cer"; Flags: dontcopy
#endif

[Dirs]
Name: "{app}\models"
Name: "{app}\engine"
Name: "{app}\chats"

[Icons]
Name: "{autoprograms}\Offline Chatter"; Filename: "{app}\OfflineChatter.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Offline Chatter"; Filename: "{app}\OfflineChatter.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\OfflineChatter.exe"; Description: "{cm:LaunchProgram,Offline Chatter}"; Flags: nowait postinstall skipifsilent

; Данные пользователя (модели, чаты, настройки) при удалении не трогаем — удаляются только файлы установки.

[Code]
const
  // Microsoft Root Certificate Authority 2011 — корень подписи установщика .NET Framework 4.8
  MsRoot2011 = '8F43288AD272F3103B6FB1428485EA3014C0BCFE';

var
  Net48JustInstalled: Boolean;

function IsNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function IsWin7WithoutSp1: Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := (V.Major = 6) and (V.Minor = 1) and (V.ServicePackMajor < 1);
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if IsWin7WithoutSp1 then
  begin
    if SuppressibleMsgBox(CustomMessage('NoSp1'), mbError, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://www.catalog.update.microsoft.com/Search.aspx?q=KB976932', '', '', SW_SHOWNORMAL, ewNoWait, Code);
    Result := False;
    exit;
  end;
#ifndef Net48
  if not IsNet48Installed then
    Result := SuppressibleMsgBox(CustomMessage('NoNet48'), mbConfirmation, MB_YESNO, IDYES) = IDYES;
#endif
end;

#ifdef Net48
function HasMsRoot2011: Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\Microsoft\SystemCertificates\';
  Result := RegKeyExists(HKLM, Key + 'ROOT\Certificates\' + MsRoot2011) or RegKeyExists(HKLM, Key + 'AuthRoot\Certificates\' + MsRoot2011);
  if not Result and IsWin64 then
    Result := RegKeyExists(HKLM64, Key + 'ROOT\Certificates\' + MsRoot2011) or RegKeyExists(HKLM64, Key + 'AuthRoot\Certificates\' + MsRoot2011);
end;

// .NET ставится до копирования файлов программы. Права администратора нужны только для этого шага —
// сама программа по-прежнему ставится для текущего пользователя. Один запрос UAC на сертификат и .NET.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Script, Text: String;
  NeedRoot: Boolean;
  Code: Integer;
begin
  Result := '';
  if IsNet48Installed then exit;
  NeedRoot := not HasMsRoot2011;
  Text := CustomMessage('InstallNet48');
  if NeedRoot then Text := Text + CustomMessage('InstallRootCert');
  if SuppressibleMsgBox(Text + CustomMessage('ContinueQuestion'), mbConfirmation, MB_YESNO, IDYES) <> IDYES then exit;

  ExtractTemporaryFile('ndp48.exe');
  Script := ExpandConstant('{tmp}\install-net48.cmd');
  Text := '@echo off' + #13#10;
  if NeedRoot then
  begin
    ExtractTemporaryFile('MicRooCerAut2011.cer');
    Text := Text + 'certutil -addstore -f Root "' + ExpandConstant('{tmp}\MicRooCerAut2011.cer') + '"' + #13#10;
  end;
  Text := Text + '"' + ExpandConstant('{tmp}\ndp48.exe') + '" /passive /norestart' + #13#10 + 'exit /b %errorlevel%' + #13#10;
  SaveStringToFile(Script, Text, False);

  // /passive — установщик .NET показывает свой прогресс без вопросов
  if not ShellExec('runas', ExpandConstant('{cmd}'), '/c ""' + Script + '""', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Elevation for .NET Framework 4.8 was refused: ' + IntToStr(Code));

  // Код выхода повышенного процесса недоступен — проверяем результат по реестру
  Net48JustInstalled := IsNet48Installed;
  if not Net48JustInstalled then
    SuppressibleMsgBox(CustomMessage('Net48Failed'), mbError, MB_OK, IDOK);
end;

// После установки .NET 4.8 Windows обычно требует перезагрузку — Inno Setup предложит её в конце
function NeedRestart: Boolean;
begin
  Result := Net48JustInstalled;
end;
#endif