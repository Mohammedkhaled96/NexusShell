; NexusShell Inno Setup Script (Silent Installer for Winget & Automated Upgrades)
#define MyAppName "NexusShell"
#ifndef MyAppVersion
  #define MyAppVersion "4.1.1"
#endif
#define MyAppPublisher "Farid Mohammed & Mohammed Khaled"
#define MyAppURL "https://github.com/Mohammedkhaled96/NexusShell"
#define MyAppExeName "NexusShell.App.exe"

#ifndef SourceDir
  #define SourceDir "..\..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\output"
#endif

[Setup]
AppId={{FBF2F82E-22A4-4369-958F-E726692B4755}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=NexusShell_Silent_Setup
Compression=lzma2/max
SolidCompression=no
WizardStyle=modern
SetupLogging=yes
DirExistsWarning=no
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "Add application directory to the system PATH"; GroupDescription: "Advanced Options:"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#MyAppExeName}"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
var
  IsUpgrade: Boolean;
  InstallLocation: String;

function GetPreviousInstallPath(var Path: String): Boolean;
var
  AppIdKey, AppIdKeyTypo: String;
begin
  Result := False;
  AppIdKey := '{FBF2F82E-22A4-4369-958F-E726692B4755}_is1';
  AppIdKeyTypo := '{FBF2F82E-22A4-4369-958F-E726692B4755}}_is1';

  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKey, 'InstallLocation', Path) then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKey, 'InstallLocation', Path) then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKeyTypo, 'InstallLocation', Path) then
  begin
    Result := True;
    Exit;
  end;

  if RegQueryStringValue(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdKey, 'InstallLocation', Path) then
  begin
    Result := True;
    Exit;
  end;
end;

function InitializeSetup(): Boolean;
begin
  if GetPreviousInstallPath(InstallLocation) then
  begin
    IsUpgrade := True;
    StringChange(InstallLocation, '"', '');
  end
  else
  begin
    IsUpgrade := False;
  end;
  Result := True;
end;

procedure InitializeWizard();
begin
  if IsUpgrade then
  begin
    WizardForm.DirEdit.Text := InstallLocation;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if IsUpgrade then
  begin
    case PageID of
      wpWelcome: Result := True;
      wpInfoBefore: Result := True;
      wpSelectDir: Result := True;
      wpSelectComponents: Result := True;
      wpSelectProgramGroup: Result := True;
      wpSelectTasks: Result := True;
    end;
  end;
end;

const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure AddToPath;
var
  OldPath, NewPath: string;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', OldPath) then
  begin
    NewPath := OldPath;
    if Pos(ExpandConstant('{app}'), NewPath) = 0 then
    begin
      if Length(NewPath) > 0 then
        if NewPath[Length(NewPath)] <> ';' then
          NewPath := NewPath + ';';
      NewPath := NewPath + ExpandConstant('{app}');
      RegWriteStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', NewPath);
    end;
  end;
end;

procedure RemoveFromPath;
var
  OldPath, NewPath: string;
  AppPath: string;
  P: integer;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', OldPath) then
  begin
    AppPath := ExpandConstant('{app}');
    P := Pos(AnsiLowerCase(AppPath), AnsiLowerCase(OldPath));
    if P > 0 then
    begin
      NewPath := Copy(OldPath, 1, P - 1);
      if (P + Length(AppPath)) <= Length(OldPath) then
      begin
        if OldPath[P + Length(AppPath)] = ';' then
          NewPath := NewPath + Copy(OldPath, P + Length(AppPath) + 1, Length(OldPath))
        else
          NewPath := NewPath + Copy(OldPath, P + Length(AppPath), Length(OldPath));
      end;
      if (Length(NewPath) > 0) and (NewPath[Length(NewPath)] = ';') then
        NewPath := Copy(NewPath, 1, Length(NewPath) - 1);
      RegWriteStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', NewPath);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddToPath;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveFromPath;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpReady) and IsUpgrade then
  begin
    WizardForm.NextButton.Caption := '&Update';
    WizardForm.PageNameLabel.Caption := 'Ready to Update';
    WizardForm.PageDescriptionLabel.Caption := 'Setup is now ready to update {#MyAppName} on your computer.';
  end;
end;
