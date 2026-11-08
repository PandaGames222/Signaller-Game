#ifndef AppVersion
  #define AppVersion "1.4.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{9A6F11C8-1E4D-44BC-AFE8-059DFE41F4AB}
AppName=Signaller
AppVersion={#AppVersion}
AppPublisher=PandaGames222
SetupIconFile=..\Assets\Signaller.ico
DefaultDirName={localappdata}\Programs\Signaller
DefaultGroupName=Signaller
UninstallDisplayIcon={app}\Signaller.exe
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Signaller-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Signaller"; Filename: "{app}\Signaller.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Signaller"; Filename: "{app}\Signaller.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Signaller.exe"; Description: "Launch Signaller"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyUninstaller: string;
  ExitCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if CompareText(ExpandConstant('{app}'),
      ExpandConstant('{localappdata}\Programs\Signaller')) = 0 then
    begin
      LegacyUninstaller := ExpandConstant('{localappdata}\Programs\Signaller\Uninstall Signaller.exe');
      if FileExists(LegacyUninstaller) then
      begin
        if not Exec(LegacyUninstaller, '/S', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
          RaiseException('The previous Signaller installation could not be removed.');
        if ExitCode <> 0 then
          RaiseException('The previous Signaller installation could not be removed.');
      end;
    end;
  end;
end;
