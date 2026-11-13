; Rewind's installer. release.ps1 builds it after publishing; by hand, from the windows folder:
;   ISCC.exe /DAppVersion=1.3.0 /DArch=x64 /DBinDir=dist\win-x64 installer\Rewind.iss

#ifndef AppVersion
  #error Pass the version, e.g. /DAppVersion=1.3.0
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef BinDir
  #define BinDir "..\dist\win-" + Arch
#endif

[Setup]
AppId={{5C82EE1B-8C86-4157-966F-0F973D4C317F}
AppName=Rewind
AppVersion={#AppVersion}
AppVerName=Rewind {#AppVersion}
AppPublisher=Rewind
AppPublisherURL=https://github.com/browneie/Rewind
AppSupportURL=https://github.com/browneie/Rewind/issues
AppUpdatesURL=https://github.com/browneie/Rewind/releases
VersionInfoVersion={#AppVersion}
; Installs for the current user (no admin prompt); /ALLUSERS installs to Program Files instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\Rewind
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Rewind-{#AppVersion}-Windows-{#Arch}-Setup
SetupIconFile=..\Assets\Rewind.ico
UninstallDisplayIcon={app}\Rewind.exe
UninstallDisplayName=Rewind
WizardStyle=modern dynamic windows11
WizardImageFile=
WizardSmallImageFile=..\..\docs\icon.png
Compression=lzma2/max
SolidCompression=yes
; Rewind is closed by the code below, which lets a recording in progress finish saving.
CloseApplications=no

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BinDir}\Rewind.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Rewind"; Filename: "{app}\Rewind.exe"
Name: "{autodesktop}\Rewind"; Filename: "{app}\Rewind.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Rewind.exe"; Description: "{cm:LaunchProgram,Rewind}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function RunningFrom(const Exe: String): Boolean;
var
  Locator, Service, Procs, Path: Variant;
  I: Integer;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''Rewind.exe''');
    for I := 0 to Procs.Count - 1 do
    begin
      Path := Procs.ItemIndex(I).ExecutablePath;
      if not VarIsNull(Path) and (CompareText(Path, Exe) = 0) then
        Result := True;
    end;
  except
  end;
end;

{ Asks the Rewind installed at Exe to quit, like Quit in its tray menu, and waits for it. Copies of Rewind
  elsewhere are left alone. }
function CloseRewind(const Exe: String): Boolean;
var
  Code, I: Integer;
begin
  Result := True;
  if not RunningFrom(Exe) then
    Exit;
  Exec(Exe, '--action quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 80 do
  begin
    if not RunningFrom(Exe) then
      Exit;
    Sleep(250);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseRewind(ExpandConstant('{app}\Rewind.exe')) then
    Result := 'Rewind is still running. Quit it from its icon in the notification area, then run Setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseRewind(ExpandConstant('{app}\Rewind.exe'));
  if not Result then
    SuppressibleMsgBox('Rewind is still running. Quit it from its icon in the notification area, then try again.',
      mbError, MB_OK, IDOK);
end;

{ Settings and clips stay; only the "start with Windows" entry pointing at this copy goes. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, 'Rewind', Value) and
     (Pos(Uppercase(ExpandConstant('{app}\Rewind.exe')), Uppercase(Value)) > 0) then
    RegDeleteValue(HKCU, RunKey, 'Rewind');
end;
