; Intonaco Windows installer
;
; Build with installer\Build-Installer.ps1. The payload must be a
; self-contained, non-single-file win-x64 publish directory. User settings
; deliberately live outside {app} (%LocalAppData%\Intonaco), so upgrades,
; reinstalls and uninstall do not delete them.

#ifndef MyAppVersion
  #define MyAppVersion "2.1.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\installer-app"
#endif

#ifndef OutputDir
  #define OutputDir "..\release"
#endif

#define MyAppName "Intonaco"
#define MyAppPublisher "Intonaco Contributors"
#define MyAppURL "https://github.com/p4yy/Intonaco"
#define MyAppExeName "Intonaco.exe"

[Setup]
AppId={{D28B4A1C-5663-409B-A94B-6BA5E2BB314F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Intonaco live wallpaper for Windows
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=no
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Intonaco-v{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\Intonaco\Assets\intonaco.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.18362
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
ShowLanguageDialog=no
ChangesAssociations=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
CreateUninstallRegKey=yes
Uninstallable=yes
CloseApplications=yes
CloseApplicationsFilter=Intonaco.exe
RestartApplications=no
AppMutex=Intonaco_SingleInstance_Mutex
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nIntonaco is self-contained: no .NET installation or administrator account is required. Your Intonaco settings are stored separately and are preserved during upgrades and uninstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Lightweight live wallpaper for Windows"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Lightweight live wallpaper for Windows"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Intonaco's own Start with Windows option writes this per-user value.
      Remove only that value; %LocalAppData%\Intonaco settings stay intact. }
    RegDeleteValue(
      HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'Intonaco');
  end;
end;
