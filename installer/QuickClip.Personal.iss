#ifndef AppVersion
  #error Build the installer with scripts\package.ps1 -Personal
#endif

[Setup]
AppId={{90FB3786-4F27-41D1-B7D6-44E671DE28CA}
AppName=QuickClip Personal
AppVersion={#AppVersion}
AppVerName=QuickClip Personal {#DisplayVersion}
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=QuickClip Personal setup (in-place update v1)
DefaultDirName={localappdata}\Programs\QuickClip Personal
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=no
AllowRootDirectory=no
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=QuickClip-Personal-{#AppVersion}-setup
SetupIconFile=..\src\QuickClip\Assets\QuickClip.ico
UninstallDisplayIcon={app}\QuickClip.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
CloseApplications=yes
RestartApplications=no
SetupMutex=Local\QuickClip.Personal.Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\QuickClip Personal"; Filename: "{app}\QuickClip.exe"

[Run]
Filename: "{app}\QuickClip.exe"; Description: "Open QuickClip Personal"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\QuickClip.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitQuickClipPersonal"

[Code]
const
  InstallKey = 'Software\QuickClip Personal\Install';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'QuickClip Personal';
  StartupArgument = ' --windows-startup';

function StartupCommand(Exe: String): String;
begin
  Result := '"' + Exe + '"' + StartupArgument;
end;

{ Check the selected folder (or nearest existing parent) before extracting
  application files. Never change ACLs or elevate to bypass folder permissions. }
function DestinationError(Destination: String): String;
var
  Parent, Probe, ProbeFile, NextParent: String;
  I: Integer;
begin
  Result := '';
  Parent := Destination;
  while not DirExists(Parent) do
  begin
    if FileExists(Parent) then
    begin
      Result := 'A file already exists at the selected installation path: ' + Parent;
      exit;
    end;
    NextParent := ExtractFileDir(Parent);
    if (NextParent = '') or (CompareText(NextParent, Parent) = 0) then
    begin
      Result := 'The selected drive or folder is unavailable: ' + Destination;
      exit;
    end;
    Parent := NextParent;
  end;
  Probe := '';
  for I := 1 to 10 do
  begin
    Probe := AddBackslash(Parent) + '.quickclip-write-test-' +
      GetDateTimeString('yyyymmddhhnnss', '-', '-') + '-' + IntToStr(I);
    if not DirExists(Probe) and not FileExists(Probe) then break;
    Probe := '';
  end;
  if Probe <> '' then
    if CreateDir(Probe) then
    begin
      ProbeFile := AddBackslash(Probe) + 'probe.txt';
      if SaveStringToFile(ProbeFile, 'QuickClip destination test', False) then
        if DeleteFile(ProbeFile) then
          if RemoveDir(Probe) then exit;
      { Only clean up the temporary test folder that this run created. }
      if FileExists(ProbeFile) then DeleteFile(ProbeFile);
      RemoveDir(Probe);
    end;
  Result := 'QuickClip cannot write to the selected installation folder or its parent:' + #13#10 +
    Destination + #13#10#13#10 +
    'Choose a folder owned by your Windows account on any local drive (for example F:\Games\QuickClip Personal), ' +
    'or use the default location. If access is denied, ask the folder owner or administrator for write permission. ' +
    'QuickClip installs for your current account and does not change drive permissions.';
  Log(Result);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorText: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    ErrorText := DestinationError(WizardDirValue);
    if ErrorText <> '' then
    begin
      SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Previous, Exe, FileVersion: String;
begin
  Previous := ExpandConstant('{param:QCUPDATE|}');
  if Previous <> '' then
  begin
    Result := 'The existing QuickClip Personal installation has changed. Use the manual setup.';
    if not RegQueryStringValue(HKCU, InstallKey, 'Executable', Exe) then exit;
    if CompareText(AddBackslash(WizardDirValue) + 'QuickClip.exe', Exe) <> 0 then exit;
    if not FileExists(AddBackslash(WizardDirValue) + 'quickclip-install-files.txt') then exit;
    if not GetVersionNumbersString(Exe, FileVersion) then exit;
    if FileVersion <> Previous + '.0' then exit;
    if CheckForMutexes('Local\QuickClip.Personal.Instance') then
    begin
      Result := 'QuickClip Personal is still running. Close it before installing the update.';
      exit;
    end;
  end;
  Result := DestinationError(WizardDirValue);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  OldExe, NewExe, OldCommand, NewCommand: String;
begin
  if CurStep <> ssPostInstall then exit;
  NewExe := ExpandConstant('{app}\QuickClip.exe');
  if not RegQueryStringValue(HKCU, InstallKey, 'Executable', OldExe) then OldExe := '';
  if not RegWriteStringValue(HKCU, InstallKey, 'Executable', NewExe) then
    RaiseException('Could not register the QuickClip Personal installation.');

  if (OldExe <> '') and (CompareText(OldExe, NewExe) <> 0) and
     RegQueryStringValue(HKCU, RunKey, RunValue, OldCommand) and
     (CompareText(OldCommand, StartupCommand(OldExe)) = 0) then
  begin
    NewCommand := StartupCommand(NewExe);
    if Length(NewCommand) > 260 then
    begin
      if not RegDeleteValue(HKCU, RunKey, RunValue) then
        RaiseException('Could not remove the previous Windows startup entry.');
    end
    else if not RegWriteStringValue(HKCU, RunKey, RunValue, NewCommand) then
      RaiseException('Could not move the Windows startup entry.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe, InstalledExe, Command: String;
begin
  if CurUninstallStep <> usUninstall then exit;
  Exe := ExpandConstant('{app}\QuickClip.exe');
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) and
     (CompareText(Command, StartupCommand(Exe)) = 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
  if RegQueryStringValue(HKCU, InstallKey, 'Executable', InstalledExe) and (CompareText(InstalledExe, Exe) = 0) then
  begin
    RegDeleteValue(HKCU, InstallKey, 'Executable');
    RegDeleteKeyIfEmpty(HKCU, InstallKey);
  end;
end;
