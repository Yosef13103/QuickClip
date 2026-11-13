#ifndef AppVersion
  #error Build the installer with scripts\package.ps1 -Personal
#endif

[Setup]
AppId={{90FB3786-4F27-41D1-B7D6-44E671DE28CA}
AppName=QuickClip Personal
AppVersion={#AppVersion}
AppVerName=QuickClip Personal {#DisplayVersion}
VersionInfoVersion={#NumericVersion}
DefaultDirName={localappdata}\Programs\QuickClip Personal
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
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
