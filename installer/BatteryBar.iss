; Установщик BatteryBar (Inno Setup 6). Собирается из tools\release.ps1:
;   ISCC /DAppVersion=0.1.0 installer\BatteryBar.iss
; Ставит программу для текущего пользователя, без прав администратора.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8C4A6E1B-2F3D-4B7A-9E1C-5D6F7A8B9C0D}
AppName=BatteryBar
AppVersion={#AppVersion}
AppVerName=BatteryBar {#AppVersion}
AppPublisher=RonRealiza
AppPublisherURL=https://github.com/RonRealiza/BatteryBar
AppSupportURL=https://github.com/RonRealiza/BatteryBar/issues
AppUpdatesURL=https://github.com/RonRealiza/BatteryBar/releases
DefaultDirName={localappdata}\Programs\BatteryBar
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=BatteryBar-{#AppVersion}-Setup
SetupIconFile=..\assets\BatteryBar.ico
UninstallDisplayIcon={app}\BatteryBar.exe
UninstallDisplayName=BatteryBar
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
ShowLanguageDialog=no
CloseApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutostartTask=Запускать BatteryBar вместе с Windows
en.AutostartTask=Start BatteryBar with Windows
ru.DotNetMissing=Для работы BatteryBar нужен .NET 9 Desktop Runtime (бесплатно, от Microsoft).%n%nОткрыть страницу загрузки? Установку BatteryBar можно продолжить и поставить .NET позже.
en.DotNetMissing=BatteryBar needs the .NET 9 Desktop Runtime (free, from Microsoft).%n%nOpen the download page? You can continue installing BatteryBar and install .NET later.

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BatteryBar"; Filename: "{app}\BatteryBar.exe"
Name: "{autodesktop}\BatteryBar"; Filename: "{app}\BatteryBar.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BatteryBar"; ValueData: """{app}\BatteryBar.exe"""; Tasks: autostart

[Run]
Filename: "{app}\BatteryBar.exe"; Description: "{cm:LaunchProgram,BatteryBar}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM BatteryBar.exe /F"; Flags: runhidden; RunOnceId: "StopBatteryBar"

[Code]
function DotNetDesktopInstalled: Boolean;
var
  R: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), R);
  if Result then
    FindClose(R);
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if not DotNetDesktopInstalled then
    if SuppressibleMsgBox(CustomMessage('DotNetMissing'), mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/9.0', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

// Запущенный BatteryBar держит файлы — закрываем его перед копированием (это значок в трее, окна у него нет).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM BatteryBar.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Result := '';
end;

// Автозапуск мог включить и сам BatteryBar в настройках — при удалении убираем его в любом случае.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BatteryBar');
end;
