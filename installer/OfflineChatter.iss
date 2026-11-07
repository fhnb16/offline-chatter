; Установщик Offline Chatter (Inno Setup 6).
; По умолчанию — для текущего пользователя, без прав администратора, в %LOCALAPPDATA%\Programs\OfflineChatter
; (туда же программа предлагает перенести себя при запуске из «Загрузок»). В окне выбора можно поставить для всех
; пользователей в Program Files — тогда данные будут храниться в %LOCALAPPDATA%\OfflineChatter.
; Сборка (CI делает это сам):
;   iscc /DAppVersion=0.1.42 /DStageDir=..\out\stage\OfflineChatter /DOutDir=..\out installer\OfflineChatter.iss

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
OutputBaseFilename=OfflineChatter-{#AppVersion}-Setup
SetupIconFile=..\src\OfflineChatter\Assets\app.ico
UninstallDisplayIcon={app}\OfflineChatter.exe
UninstallDisplayName=Offline Chatter
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 7 SP1 и новее
MinVersion=6.1sp1
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
ru.NoNet48=Для работы Offline Chatter нужен .NET Framework 4.8. Он уже есть в Windows 10 (1903+) и Windows 11, для Windows 7/8.1 его можно скачать на сайте Microsoft.%n%nПродолжить установку?
en.NoNet48=Offline Chatter needs .NET Framework 4.8. It is built into Windows 10 (1903+) and Windows 11; for Windows 7/8.1 download it from Microsoft.%n%nContinue the installation?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
function IsNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsNet48Installed then
    Result := MsgBox(CustomMessage('NoNet48'), mbConfirmation, MB_YESNO) = IDYES;
end;
