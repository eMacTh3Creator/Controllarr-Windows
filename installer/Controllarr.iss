#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef Runtime
  #error Runtime is required
#endif

[Setup]
AppId={code:GetAppId}
UsePreviousLanguage=no
AppName=Controllarr
AppVersion={#AppVersion}
AppPublisher=Controllarr
AppPublisherURL=https://emacth3creator.github.io/Controllarr-Windows/
AppSupportURL=https://github.com/eMacTh3Creator/Controllarr-Windows/issues
AppUpdatesURL=https://github.com/eMacTh3Creator/Controllarr-Windows/releases
DefaultDirName={localappdata}\Programs\Controllarr
DefaultGroupName=Controllarr
PrivilegesRequired=lowest
#if Runtime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputBaseFilename=Controllarr-{#AppVersion}-{#Runtime}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
SetupIconFile=..\src\Controllarr.App\Assets\controllarr.ico
UninstallDisplayIcon={app}\Controllarr.exe
AppMutex=Global\Controllarr_SingleInstance_Mutex
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked; Check: not IsLab

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ImportProfile.ps1"; Flags: dontcopy

[Icons]
Name: "{userprograms}\Controllarr"; Filename: "{app}\Controllarr.exe"; Check: not IsLab
Name: "{userdesktop}\Controllarr"; Filename: "{app}\Controllarr.exe"; Tasks: desktopicon; Check: not IsLab

[Run]
Filename: "{app}\Controllarr.exe"; Description: "Launch Controllarr"; Flags: nowait postinstall skipifsilent; Check: CanLaunch

[Code]
var
  ProfilePage: TInputOptionWizardPage;
  SourcePage: TInputDirWizardPage;
  Passwords: TNewCheckBox;
  ImportFailed: Boolean;

function IsLab: Boolean;
begin
  Result := ExpandConstant('{param:LABID|}') <> '';
end;

function GetAppId(Param: String): String;
begin
  Result := 'Controllarr.Desktop.8D638AE6-41EA-48F0-A6F5-562B6C6845A8';
  if IsLab then Result := Result + '.Lab.' + ExpandConstant('{param:LABID|}');
end;

function ProfileTarget: String;
begin
  Result := ExpandConstant('{userappdata}\Controllarr');
  if IsLab then Result := ExpandConstant('{param:PROFILEDIR|}')
end;

function ImportSelected: Boolean;
begin
  Result := ProfilePage.SelectedValueIndex = 1;
end;

function CanLaunch: Boolean;
begin
  Result := not ImportFailed and not IsLab;
end;

procedure InitializeWizard;
begin
  ProfilePage := CreateInputOptionPage(wpSelectDir, 'Settings and torrent library',
    'Keep your current profile or import a previous build',
    'Existing settings, categories, torrents and saved passwords are kept by default. Import copies profile metadata only; downloaded files are not moved or deleted. Exit the previous app first (including its tray icon).', True, False);
  ProfilePage.Add('Keep existing settings (recommended; automatically uses the current AppData profile)');
  ProfilePage.Add('Import a previous Controllarr Windows profile folder');
  ProfilePage.SelectedValueIndex := 0;
  SourcePage := CreateInputDirPage(ProfilePage.ID, 'Import previous profile',
    'Choose the folder containing state.json',
    'Select a backup or custom profile folder, not the application folder. Existing target metadata will be backed up. Torrent payload paths stay unchanged. Import from another PC/account requires entering passwords again.', False, '');
  SourcePage.Add('Previous profile folder:');
  SourcePage.Values[0] := ExpandConstant('{userappdata}\Controllarr');
  Passwords := TNewCheckBox.Create(WizardForm);
  Passwords.Parent := SourcePage.Surface;
  Passwords.Top := SourcePage.Edits[0].Top + SourcePage.Edits[0].Height + ScaleY(16);
  Passwords.Width := SourcePage.SurfaceWidth;
  Passwords.Caption := 'Import saved passwords (same Windows account only)';
  Passwords.Checked := True;
  if ExpandConstant('{param:IMPORTPROFILE|}') <> '' then begin
    ProfilePage.SelectedValueIndex := 1;
    SourcePage.Values[0] := ExpandConstant('{param:IMPORTPROFILE|}');
  end;
  if ExpandConstant('{param:SKIPCREDENTIALS|0}') = '1' then Passwords.Checked := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SourcePage.ID) and not ImportSelected;
end;

function RunImport(ValidateOnly: Boolean): String;
var
  Params, ResultPath: String;
  Report: AnsiString;
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('ImportProfile.ps1');
  ResultPath := ExpandConstant('{tmp}\profile-result.txt');
  DeleteFile(ResultPath);
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(ExpandConstant('{tmp}\ImportProfile.ps1')) +
    ' -SourceDirectory ' + AddQuotes(SourcePage.Values[0]) + ' -TargetDirectory ' + AddQuotes(ProfileTarget) + ' -ResultFile ' + AddQuotes(ResultPath);
  if ValidateOnly then Params := Params + ' -ValidateOnly';
  if not Passwords.Checked then Params := Params + ' -SkipCredentials';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := 'Could not start profile import. Existing profile was not changed.'
  else if Code <> 0 then begin
    if LoadStringFromFile(ResultPath, Report) then Result := UTF8Decode(Report)
    else Result := 'Profile import failed. Keep the existing profile or choose a valid backup.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ImportSelected then Result := RunImport(True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Error: String;
begin
  if (CurStep = ssPostInstall) and ImportSelected then begin
    Error := RunImport(False);
    if Error <> '' then begin
      ImportFailed := True;
      RaiseException('Application installed, but profile import failed. The app will not be launched. ' + Error);
    end;
  end;
end;
