// Included in [Code]. Only exact registered AppId installations are upgrades.
// Destructive state choices are interactive, independent and never remembered.

function IsSafeLocalFolder(const Path: String): Boolean;
var Normalized: String;
begin
  Result := False;
  if (Trim(Path) = '') or PathHasInvalidCharacters(Path, True) then exit;
  if not PathIsRooted(Path) or (Length(ExtractFileDrive(Path)) <> 2) then exit;
  Normalized := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  Result := Length(Normalized) > Length(AddBackslash(ExtractFileDrive(Normalized)));
end;

function VerifyDataHelper(const Path: String): Boolean; forward;

type
  TReceiptGuid = record
    A: Cardinal; B, C: Word; D: array[0..7] of Byte;
  end;
  TReceiptFileInfo = record
    Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow,
    WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow: Cardinal;
  end;

function ReceiptCreateGuid(var Guid: TReceiptGuid): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';
function ReceiptCreateFile(Name: String; Access, Share: Cardinal; Security: Integer;
  Creation, Flags: Cardinal; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function ReceiptFileInfo(Handle: Integer; var Info: TReceiptFileInfo): Boolean;
  external 'GetFileInformationByHandle@kernel32.dll stdcall';
function ReceiptCloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function NewReceiptNonce: String;
var Guid: TReceiptGuid;
begin
  if ReceiptCreateGuid(Guid) <> 0 then RaiseException(SwT('The protected data-safety receipt could not be prepared. No files were changed.'));
  Result := LowerCase(Format('%.8x%.4x%.4x%.2x%.2x%.2x%.2x%.2x%.2x%.2x%.2x', [Guid.A,
    Guid.B, Guid.C, Guid.D[0], Guid.D[1], Guid.D[2], Guid.D[3], Guid.D[4], Guid.D[5], Guid.D[6], Guid.D[7]]));
end;

function LockReceipt(const Path: String): Integer;
var Info: TReceiptFileInfo;
begin
  // Read-only, no-follow and shared-read only. Retain the handle while reading
  // INI fields, so the original user cannot turn the receipt into a reparse point.
  Result := ReceiptCreateFile(Path, $80000000, 1, 0, 3, $00200000, 0);
  if Result = -1 then RaiseException(SwT('The protected data-safety receipt could not be read safely. No optional data changes were requested.'));
  if not ReceiptFileInfo(Result, Info) or ((Info.Attributes and $410) <> 0) or
    (Info.SizeHigh <> 0) or (Info.SizeLow > 131072) then begin
    ReceiptCloseHandle(Result);
    RaiseException(SwT('The protected data-safety receipt could not be read safely. No optional data changes were requested.'));
  end;
end;

function PinReceiptDirectory(const Path: String): Integer;
var Info: TReceiptFileInfo;
begin
  // The protected folder lives beneath user-owned Temp. Pin the actual plain
  // directory throughout the workflow: share read/write, but never deletion,
  // rename or replacement through its writable immediate parent.
  Result := ReceiptCreateFile(Path, $80000000, 3, 0, 3, $02200000, 0);
  if Result = -1 then RaiseException(SwT('The protected data-safety receipt could not be read safely. No optional data changes were requested.'));
  if not ReceiptFileInfo(Result, Info) or ((Info.Attributes and $400) <> 0) or
    ((Info.Attributes and $10) = 0) then begin
    ReceiptCloseHandle(Result);
    RaiseException(SwT('The protected data-safety receipt could not be read safely. No optional data changes were requested.'));
  end;
end;

procedure EnsureLifetimeReceiptPin;
begin
  // Keep Inno's initially created protected directory pinned across wizard
  // waits and between identity, validation and apply. A user's Temp parent can
  // otherwise rename the protected child using DELETE_CHILD between phases.
  if not ReceiptTempPinned then begin
    LifetimeReceiptTempHandle := PinReceiptDirectory(ExpandConstant('{tmp}'));
    ReceiptTempPinned := True;
  end;
end;

procedure ReleaseLifetimeReceiptPin;
begin
  if ReceiptTempPinned then begin
    ReceiptTempPinned := False;
    ReceiptCloseHandle(LifetimeReceiptTempHandle);
  end;
end;

<event('DeinitializeSetup')>
procedure ReleaseSetupReceiptPin;
begin
  ReleaseLifetimeReceiptPin;
end;

<event('DeinitializeUninstall')>
procedure ReleaseUninstallReceiptPin;
begin
  ReleaseLifetimeReceiptPin;
end;

function PrepareOriginalReceipt(const Path, Nonce: String; var GrantedSid: String): Boolean;
var Arguments: String; ResultCode, Handle: Integer;
begin
  Result := False; GrantedSid := '';
  if not VerifyDataHelper(DataHelperPath) then exit;
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(DataHelperPath) +
    ' -PrepareProtectedReceipt -ReceiptPath ' + AddQuotes(Path) + ' -ReceiptNonce ' + Nonce;
  // Elevated preparation is metadata-only. It never resolves/touches app data.
  // It leaves the helper and parent protected and grants file Write only to the
  // one ordinary RX SID already supplied by Inno's protected parent ACL.
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or
    (ResultCode <> 0) then begin Log('Protected receipt preparation failed; code: ' + IntToStr(ResultCode)); exit; end;
  Handle := LockReceipt(Path);
  try
    GrantedSid := GetIniString('Transport', 'UserSid', '', Path);
    Result := (Copy(GrantedSid, 1, 9) = 'S-1-5-21-') and
      SameText(GetIniString('Transport', 'ReceiptNonce', '', Path), Nonce) and
      (GetIniString('Result', 'Success', '1', Path) = '0');
  finally ReceiptCloseHandle(Handle); end;
end;

procedure ResolveInstallDataRoot;
var Arguments, IdentityReceipt, SourceHelper, Nonce, GrantedSid: String;
  ResultCode, Handle, TempHandle: Integer; Started: Boolean;
begin
  PrivateUserVerified := False;
  SupportedDataRoot := False;
  PrivateUserSid := ''; PrivateUserAccount := '';
  SelectedDataRoot := 'Unverified Windows account (data kept)';
  // This is a metadata-only fallback, not the application's data location.
  PreflightDataRoot := ExpandConstant('{commonappdata}\SayWhat');
  DataExecAsOriginalUser := IsAdmin and not IsUninstaller;
  TempHandle := -1;
  try
   try
    TempHandle := PinReceiptDirectory(ExpandConstant('{tmp}'));
    EnsureLifetimeReceiptPin;
    if IsUninstaller then begin
      SourceHelper := ExpandConstant('{app}\provisioning\manage-install-data.ps1');
      if not VerifyDataHelper(SourceHelper) then exit;
      DataHelperPath := ExpandConstant('{tmp}\saywhat-uninstall-data.ps1');
      if not CopyFile(SourceHelper, DataHelperPath, True) then exit;
    end else begin
      ExtractTemporaryFile('manage-install-data.ps1');
      DataHelperPath := ExpandConstant('{tmp}\manage-install-data.ps1');
    end;
    if not VerifyDataHelper(DataHelperPath) then exit;
    IdentityReceipt := ExpandConstant('{tmp}\saywhat-data-identity.ini');
    Nonce := ''; GrantedSid := '';
    if DataExecAsOriginalUser then begin
      Nonce := NewReceiptNonce;
      if not PrepareOriginalReceipt(IdentityReceipt, Nonce, GrantedSid) then exit;
    end;
    Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(DataHelperPath) +
      ' -ResolveUserIdentity -ReceiptPath ' + AddQuotes(IdentityReceipt);
    if DataExecAsOriginalUser then Arguments := Arguments +
      ' -RequireProtectedReceipt -RequireOriginalUserToken -ExpectedUserSid ' + GrantedSid + ' -ReceiptNonce ' + Nonce;
    // Only Setup has an original-user token. Uninstall instead verifies that
    // its current SID matches this session's shell owner and fails closed when
    // UAC used another account. Never enumerate or choose another profile.
    if DataExecAsOriginalUser then
      Started := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else Started := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not Started or (ResultCode <> 0) then exit;
    Handle := LockReceipt(IdentityReceipt);
    try
    if GetIniString('Result', 'Success', '0', IdentityReceipt) <> '1' then exit;
    if DataExecAsOriginalUser and not SameText(GetIniString('Result', 'ReceiptNonce', '', IdentityReceipt), Nonce) then exit;
    SelectedDataRoot := GetIniString('Result', 'DataRoot', '', IdentityReceipt);
    PrivateUserSid := GetIniString('Result', 'UserSid', '', IdentityReceipt);
    if DataExecAsOriginalUser and not SameText(PrivateUserSid, GrantedSid) then exit;
    PrivateUserAccount := GetIniString('Result', 'Account', '', IdentityReceipt);
    PrivateUserVerified := (Copy(PrivateUserSid, 1, 6) = 'S-1-5-') and (PrivateUserAccount <> '') and IsSafeLocalFolder(SelectedDataRoot);
    SupportedDataRoot := PrivateUserVerified and (GetIniString('Result', 'Supported', '0', IdentityReceipt) = '1');
    if SupportedDataRoot then PreflightDataRoot := SelectedDataRoot;
    finally ReceiptCloseHandle(Handle); end;
  except
    Log('Private-data identity could not be verified. All personal data will be kept.');
  end;
  finally if TempHandle <> -1 then ReceiptCloseHandle(TempHandle); end;
end;

procedure ReadUserInstallRecord(Root: Integer);
var Path, Version, DisplayName, FirstCommand, OtherCommand: String;
begin
  if not RegKeyExists(Root, AppUninstallKey) then exit;
  Path := '';
  RegQueryStringValue(Root, AppUninstallKey, 'Inno Setup: App Path', Path);
  if Path = '' then RegQueryStringValue(Root, AppUninstallKey, 'InstallLocation', Path);
  RegQueryStringValue(Root, AppUninstallKey, 'DisplayVersion', Version);
  RegQueryStringValue(Root, AppUninstallKey, 'DisplayName', DisplayName);
  if HasUserRecord then begin
    if not PathSame(ExistingAppDir, Path) then AmbiguousRecord := True;
    if (ExistingVersion <> '') and (Version <> '') and not SameText(ExistingVersion, Version) then AmbiguousRecord := True;
    RegQueryStringValue(ExistingRegistryRoot, AppUninstallKey, 'UninstallString', FirstCommand);
    RegQueryStringValue(Root, AppUninstallKey, 'UninstallString', OtherCommand);
    if (FirstCommand <> '') and (OtherCommand <> '') and not SameText(Trim(FirstCommand), Trim(OtherCommand)) then AmbiguousRecord := True;
    exit;
  end;
  HasUserRecord := True;
  ExistingRegistryRoot := Root;
  if IsSafeLocalFolder(Path) then ExistingAppDir := RemoveBackslashUnlessRoot(ExpandFileName(Path))
  else AmbiguousRecord := True;
  ExistingVersion := Version;
  // The exact AppId, registered path and executable identify this installation.
  // Historical Inno DisplayName includes AppVerName (brand + version), so display
  // text is informational, never a reason to reject an otherwise valid upgrade.
end;

function ReadRegisteredUninstaller: String;
var Raw, Name, Version: String; ClosingQuote, Index: Integer;
begin
  Result := '';
  if (ExistingAppDir = '') or not RegQueryStringValue(ExistingRegistryRoot, AppUninstallKey, 'UninstallString', Raw) then exit;
  Raw := Trim(Raw);
  if Copy(Raw, 1, 1) = '"' then begin
    Delete(Raw, 1, 1);
    ClosingQuote := Pos('"', Raw);
    if (ClosingQuote = 0) or (Trim(Copy(Raw, ClosingQuote + 1, Length(Raw))) <> '') then exit;
    Raw := Copy(Raw, 1, ClosingQuote - 1);
  end;
  if not IsSafeLocalFolder(ExtractFileDir(Raw)) or not PathSame(ExtractFileDir(Raw), ExistingAppDir) then exit;
  Name := LowerCase(ExtractFileName(Raw));
  if (Length(Name) <> 12) or (Copy(Name, 1, 5) <> 'unins') or (Copy(Name, 9, 4) <> '.exe') then exit;
  for Index := 6 to 8 do if (Name[Index] < '0') or (Name[Index] > '9') then exit;
  if not FileExists(Raw) or not FileExists(ChangeFileExt(Raw, '.dat')) or not GetVersionNumbersString(Raw, Version) then exit;
  Result := ExpandFileName(Raw);
end;

function UnregisteredAppHint: String;
var Directories: array of String; Index: Integer; Exe, Version: String;
begin
  Result := '';
  SetArrayLength(Directories, 4);
  Directories[0] := ExpandConstant('{localappdata}\Programs\LocalTalk');
  Directories[1] := ExpandConstant('{localappdata}\Programs\SayWhat');
  Directories[2] := ExpandConstant('{pf64}\LocalTalk');
  Directories[3] := ExpandConstant('{pf64}\SayWhat');
  for Index := 0 to GetArrayLength(Directories) - 1 do begin
    Exe := AddBackslash(Directories[Index]) + 'SayWhat.exe';
    if not FileExists(Exe) then Exe := AddBackslash(Directories[Index]) + 'LocalTalk.exe';
    if FileExists(Exe) and GetVersionNumbersString(Exe, Version) then begin
      Result := SwT('Previous app files found without an uninstall registration:') + #13#10 +
        Directories[Index] + SwT(' (file version ') + Version + ').' + #13#10 +
        SwT('They are not treated as an installed copy and will not be uninstalled or silently taken over.');
      exit;
    end;
  end;
end;

procedure DetectInstallState;
var FileVersion, Hint: String; InstalledPacked, NewPacked: Int64; Comparison: Integer; UserRecord: Boolean;
begin
  HasUserRecord := False;
  HasVerifiedApp := False;
  AmbiguousRecord := False;
  MachineRecord := RegKeyExists(HKLM64, AppUninstallKey) or RegKeyExists(HKLM32, AppUninstallKey);
  UserRecord := RegKeyExists(HKCU64, AppUninstallKey) or RegKeyExists(HKCU32, AppUninstallKey);
  ScopeMismatch := False;
  if IsAdminInstallMode then SelectedScope := 'All users' else SelectedScope := 'Current user';
  ExistingAppDir := '';
  ExistingVersion := '';
  ExistingExe := '';
  if UserRecord and MachineRecord then AmbiguousRecord := True;
  if (IsAdminInstallMode and MachineRecord) or (not UserRecord and MachineRecord) then begin
    ExistingScope := 'All users';
    ReadUserInstallRecord(HKLM64);
    ReadUserInstallRecord(HKLM32);
  end else if UserRecord then begin
    ExistingScope := 'Current user';
    ReadUserInstallRecord(HKCU64);
    ReadUserInstallRecord(HKCU32);
  end;
  ScopeMismatch := HasUserRecord and (SelectedScope <> ExistingScope);
  if (ExistingAppDir <> '') then begin
    ExistingExe := AddBackslash(ExistingAppDir) + 'SayWhat.exe';
    if not FileExists(ExistingExe) then ExistingExe := AddBackslash(ExistingAppDir) + 'LocalTalk.exe';
    HasVerifiedApp := FileExists(ExistingExe) and GetVersionNumbersString(ExistingExe, FileVersion);
    if HasVerifiedApp then begin
      if not StrToVersion(ExistingVersion, InstalledPacked) or
        not StrToVersion(FileVersion, NewPacked) or not SamePackedVersion(InstalledPacked, NewPacked) then
        ExistingVersion := FileVersion;
    end;
  end;
  InstallAction := 'Install';
  DowngradeChoice := False;
  if HasVerifiedApp then begin
    InstallAction := 'Repair';
    if StrToVersion(ExistingVersion, InstalledPacked) and StrToVersion('{#Version}', NewPacked) then begin
      Comparison := ComparePackedVersion(InstalledPacked, NewPacked);
      if Comparison < 0 then InstallAction := 'Update'
      else if Comparison > 0 then begin InstallAction := 'Install older version'; DowngradeChoice := True; end;
    end;
    DetectionText := SwT('Installed version: ') + ExistingVersion + '  ->  {#Version}' + #13#10 +
      SwT('Current installation: ') + ExistingAppDir + #13#10;
    if InstallAction = 'Repair' then DetectionText := DetectionText + SwT('The same version is already installed. App files will be repaired.')
    else if DowngradeChoice then DetectionText := DetectionText + SwT('A newer version is installed. This would replace it with an older version.')
    else DetectionText := DetectionText + SwT('Your existing installation will be updated in place.');
  end else if HasUserRecord then begin
    InstallAction := 'Repair';
    DetectionText := SwT('A previous installation is registered, but its app files are missing or cannot be verified.') + #13#10;
    if ExistingVersion <> '' then DetectionText := DetectionText + SwT('Registered version: ') + ExistingVersion + '  ->  {#Version}' + #13#10;
    if ExistingAppDir <> '' then DetectionText := DetectionText + SwT('Registered installation: ') + ExistingAppDir + #13#10;
    DetectionText := DetectionText + SwT('Setup will repair the registered app, not infer an update from an unrelated executable.');
    if StrToVersion(ExistingVersion, InstalledPacked) and StrToVersion('{#Version}', NewPacked) then begin
      if ComparePackedVersion(InstalledPacked, NewPacked) > 0 then begin
        DowngradeChoice := True;
        DetectionText := DetectionText + #13#10 + SwT('The registered version is newer. This repair installs an older version and requires confirmation.');
      end;
    end;
  end else if DirExists(SelectedDataRoot) then begin
    DetectionText := SwT('No registered SayWhat? installation was found.') + #13#10 +
      SwT('Saved app data is available at: ') + SwT(SelectedDataRoot) + #13#10 +
      SwT('This is a reinstall. Your saved data and cached models will be reused.');
  end else DetectionText := SwT('This is a new SayWhat? installation, version {#Version}.');
  if not HasUserRecord then begin
    Hint := UnregisteredAppHint;
    if Hint <> '' then DetectionText := Hint + #13#10#13#10 + DetectionText;
  end;
  DetectionText := DetectionText + #13#10 + SwT('Selected install scope: ') + SwT(SelectedScope) + '.';
  if HasUserRecord then DetectionText := DetectionText + SwT(' Existing scope: ') + SwT(ExistingScope) + '.';
  if ScopeMismatch then DetectionText := DetectionText + #13#10 +
    SwT('To update, restart setup and choose the existing scope. Or uninstall the registered copy here first. A conflicting second installation will not be created.');
  ExistingUninstaller := ReadRegisteredUninstaller;
  RegisteredUninstallAvailable := ExistingUninstaller <> '';
  if RegisteredUninstallAvailable then UninstallerHashAtDetection := GetSHA256OfFile(ExistingUninstaller);
end;

function IsAppUpdateRequested: Boolean;
var Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if SameText(ParamStr(Index), '/AUTOUPDATE') then Result := True;
end;

function IsVerifiedAppUpdate: Boolean;
var RequestedTargetVersion: String;
begin
  Result := not AmbiguousRecord and HasVerifiedApp and not ScopeMismatch and
    (InstallAction = 'Update') and PathSame(ExistingExe, ExpandConstant('{param:SAYWHATEXE|}'));
  // New app handoffs also pin the verified download's target version. Keep
  // older Update now clients compatible: they supplied scope/path but no target.
  RequestedTargetVersion := ExpandConstant('{param:SAYWHATTO|}');
  if RequestedTargetVersion <> '' then
    Result := Result and SameText(RequestedTargetVersion, '{#Version}');
end;

function InitializeSetup: Boolean;
begin
  ResolveInstallDataRoot;
  DetectInstallState;
  Result := False;
  if AmbiguousRecord then begin
    SuppressibleMsgBox(SwT('Conflicting installation scopes, paths or identities are registered for SayWhat?.') + #13#10#13#10 +
      SwT('Use Windows Installed apps to repair or remove the old entry before continuing. No app data has been changed.'),
      mbError, MB_OK, IDOK);
    exit;
  end;
  AutoUpdateRequested := IsAppUpdateRequested;
  if AutoUpdateRequested and not IsVerifiedAppUpdate then begin
    // A handoff can only update the exact registered running copy. It cannot
    // repair, downgrade, take over another path, or choose destructive data options.
    SuppressibleMsgBox(SwT('The selected installation could not be identified safely. Close SayWhat? manually, then retry setup. No files were changed.'),
      mbError, MB_OK, IDOK);
    exit;
  end;
  Result := True;
end;

procedure ReadInstallDataChoices;
begin
  // Silent mode always preserves all data; there are no destructive CLI flags.
  ResetPreferencesChoice := False;
  ClearHistoryChoice := False;
  DeleteModelsChoice := False;
  if not AutoUpdateRequested and not WizardSilent and SupportedDataRoot then begin
    ResetPreferencesChoice := InstallDataPage.Values[0];
    ClearHistoryChoice := InstallDataPage.Values[1];
    DeleteModelsChoice := InstallDataPage.Values[2];
  end;
end;

function DataPageWarning: String;
begin
  if not PrivateUserVerified then begin
    Result := SwT('Your Windows account could not be verified. All personal data is kept. Start setup normally from your own account to choose private-data changes.');
    exit;
  end;
  if not SupportedDataRoot then begin
    Result := SwT('You use a custom data folder. Optional cleanup is disabled for safety; all data is kept.') + #13#10 +
      SwT('Custom folder: ') + SwT(SelectedDataRoot);
    exit;
  end;
  Result := SwT('Windows account: ') + PrivateUserAccount + #13#10 + SwT('Data folder: ') + SelectedDataRoot + #13#10#13#10 +
    SwT('Keep everything: leave all three options unchecked.') + #13#10 +
    SwT('Other users, imported files and Windows speech support are kept. Deleted cached models need downloading again.');
end;

procedure ReadInstallAction;
begin
  UninstallFromSetup := False;
  if not AutoUpdateRequested and not WizardSilent and RegisteredUninstallAvailable then UninstallFromSetup := InstallActionPage.Values[1];
  if InstallDataPage <> nil then begin
    InstallDataPage.CheckListBox.Enabled := SupportedDataRoot;
    InstallDataWarning.Text := DataPageWarning;
    InstallDataPage.SubCaptionLabel.Caption := SwT('Optional changes affect only the verified Windows account, even for all-users installs. Leave all boxes unchecked to keep all data.');
  end;
end;

function HasDataChoices: Boolean;
begin
  Result := ResetPreferencesChoice or ClearHistoryChoice or DeleteModelsChoice;
end;

function DataSummary: String;
begin
  Result := SwT('App preferences: ');
  if ResetPreferencesChoice then Result := Result + SwT('Reset') else Result := Result + SwT('Keep');
  Result := Result + #13#10 + SwT('Saved people and log history: ');
  if ClearHistoryChoice then Result := Result + SwT('Remove') else Result := Result + SwT('Keep');
  Result := Result + #13#10 + SwT('Downloaded model files in the app cache: ');
  if DeleteModelsChoice then Result := Result + SwT('Delete') else Result := Result + SwT('Keep');
  Result := Result + #13#10#13#10 + SwT('Imported files outside the app cache and Windows speech support are kept.');
  if PrivateUserVerified then Result := Result + #13#10 + SwT('Windows account: ') + PrivateUserAccount + #13#10 +
    SwT('Only this verified account can change; every other Windows user keeps all data.')
  else Result := Result + #13#10 + SwT('Your Windows account could not be verified. All personal data is kept. Start setup normally from your own account to choose private-data changes.');
  if DeleteModelsChoice then Result := Result + #13#10 + SwT('Deleted cached models must be downloaded again. Setup will be required next time.');
end;

function ConsentSignature: String;
begin
  Result := SelectedDataRoot + '|' + DataSummary + '|' + InstallAction;
end;

function ConfirmDataChoices(const Uninstalling: Boolean): Boolean;
var Prefix: String;
begin
  Result := True;
  if not HasDataChoices then exit;
  if Uninstalling then Prefix := SwT('After uninstalling the app, apply these optional data changes?')
  else Prefix := SwT('After the app is installed successfully, apply these optional data changes?');
  Result := MsgBox(Prefix + #13#10#13#10 + DataSummary + #13#10#13#10 +
    SwT('Data folder: ') + SwT(SelectedDataRoot) + #13#10#13#10 +
    SwT('Only the categories you selected will change. This can remove personal data and model downloads. Continue?'),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure InitializeInstallDataPage;
begin
  if ExistingAppDir <> '' then WizardForm.DirEdit.Text := ExistingAppDir
  else WizardForm.DirEdit.Text := ExpandConstant('{autopf}\SayWhat');
  if HasUserRecord and (ExistingAppDir <> '') then begin
    WizardForm.DirEdit.Enabled := False;
    WizardForm.DirBrowseButton.Enabled := False;
  end;
  InstallDetectionPage := CreateOutputMsgMemoPage(wpWelcome, SwT('Your SayWhat? installation'),
    SwT('Review the detected version, location and installation scope.'),
    SwT('Only registered copies can be updated or uninstalled. This page does not change anything.'), DetectionText);
  InstallActionPage := CreateInputOptionPage(InstallDetectionPage.ID, SwT('SayWhat? setup'),
    SwT('Choose what you want to do.'), SwT('Selected scope: ') + SwT(SelectedScope) + '.' + #13#10 +
    SwT('Updating keeps the existing app location. Uninstall removes the registered app without installing a replacement.'), True, False);
  InstallActionPage.Add(SwT(InstallAction) + ' SayWhat? {#Version}');
  InstallActionPage.Add(SwT('Uninstall the registered SayWhat? app'));
  InstallActionPage.Values[0] := not ScopeMismatch;
  InstallActionPage.Values[1] := False;
  InstallActionPage.CheckListBox.ItemEnabled[0] := not ScopeMismatch;
  InstallActionPage.CheckListBox.ItemEnabled[1] := RegisteredUninstallAvailable;
  InstallDataPage := CreateInputOptionPage(InstallActionPage.ID, SwT('Keep or clear app data'),
    SwT('Everything is kept unless you explicitly choose a change.'),
    SwT('Optional changes affect only the verified Windows account, even for all-users installs. Leave all boxes unchecked to keep all data.'), False, False);
  InstallDataPage.Add(SwT('Reset app preferences'));
  InstallDataPage.Add(SwT('Remove saved people and translation log history'));
  InstallDataPage.Add(SwT('Delete downloaded model files in the app cache'));
  InstallDataPage.Values[0] := False;
  InstallDataPage.Values[1] := False;
  InstallDataPage.Values[2] := False;
  InstallDataPage.SubCaptionLabel.Height := ScaleY(54);
  InstallDataPage.CheckListBox.Top := InstallDataPage.SubCaptionLabel.Top + InstallDataPage.SubCaptionLabel.Height + ScaleY(8);
  InstallDataPage.CheckListBox.Height := ScaleY(78);
  // Read-only scrolling keeps long account/path text reachable at large DPI.
  InstallDataWarning := TNewMemo.Create(WizardForm);
  InstallDataWarning.Parent := InstallDataPage.Surface;
  InstallDataWarning.Left := 0;
  InstallDataWarning.Top := InstallDataPage.CheckListBox.Top + InstallDataPage.CheckListBox.Height + ScaleY(12);
  InstallDataWarning.Width := InstallDataPage.SurfaceWidth;
  InstallDataWarning.Height := InstallDataPage.SurfaceHeight - InstallDataWarning.Top;
  InstallDataWarning.ReadOnly := True;
  InstallDataWarning.ScrollBars := ssVertical;
  InstallDataWarning.BorderStyle := bsNone;
  InstallDataWarning.WordWrap := True;
  InstallDataWarning.Text := DataPageWarning;
  if not SupportedDataRoot then begin
    InstallDataPage.CheckListBox.Enabled := False;
  end;
  WizardForm.WelcomeLabel2.Caption := 'SayWhat? {#Version}' + #13#10#13#10 +
    SwT('Selected installation scope: ') + SwT(SelectedScope) + '.' + #13#10#13#10 +
    SwT('Next, review detected app files and choose Install, Update, Repair or Uninstall.') + #13#10#13#10 +
    SwT('Preferences, saved people, logs and models are kept by default.');
end;

function IsUnregisteredProgramInTarget: Boolean;
var Target: String;
begin
  Target := WizardDirValue;
  Result := (FileExists(AddBackslash(Target) + 'SayWhat.exe') or FileExists(AddBackslash(Target) + 'LocalTalk.exe')) and
    not (HasUserRecord and (ExistingAppDir <> '') and PathSame(Target, ExistingAppDir));
end;

function PerformSetupUninstall: Boolean; forward;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  ReadInstallAction;
  if CurPageID = InstallActionPage.ID then begin
    if ScopeMismatch and not UninstallFromSetup then begin
      MsgBox(SwT('An existing installation uses the other scope. Restart setup and choose ') + SwT(ExistingScope) +
        SwT(', or select Uninstall here. No conflicting second copy will be installed.'), mbInformation, MB_OK);
      Result := False; exit;
    end;
  end;
  if (CurPageID = wpReady) and UninstallFromSetup then begin
    ReadInstallDataChoices;
    Result := False;
    if PerformSetupUninstall then begin
      NestedUninstallFinished := True;
      PostMessage(WizardForm.Handle, $0010, 0, 0); // WM_CLOSE: leave setup without installing files.
    end;
    exit;
  end;
  if (CurPageID = wpSelectDir) or (CurPageID = wpReady) then begin
    if IsUnregisteredProgramInTarget then begin
      MsgBox(SwT('An app executable exists in the selected folder, but no matching SayWhat? installation is registered.') + #13#10#13#10 +
        SwT('Choose a new empty installation folder, or repair the registration using the original installer. Setup will not take over an unregistered executable.'),
        mbError, MB_OK);
      Result := False;
      exit;
    end;
  end;
  if CurPageID = wpReady then begin
    ReadInstallDataChoices;
    if DowngradeChoice and (ConfirmationSignature <> ConsentSignature) then begin
      if MsgBox(SwT('A newer version (') + ExistingVersion + SwT(') is installed. Replace it with {#Version}?') + #13#10#13#10 +
        SwT('Older app versions may not understand saved settings from newer versions.'), mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) <> IDYES then begin Result := False; exit; end;
    end;
    if (ConfirmationSignature <> ConsentSignature) and not ConfirmDataChoices(False) then begin Result := False; exit; end;
    ConfirmationSignature := ConsentSignature;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  ReadInstallAction;
  ReadInstallDataChoices;
  if UninstallFromSetup then begin
    Result := SwT('Uninstall SayWhat?') + NewLine + SwT('Registered version: ') + ExistingVersion + NewLine +
      SwT('App folder: ') + ExistingAppDir + NewLine + SwT('Install scope: ') + SwT(ExistingScope) + NewLine +
      SwT('Data folder for this account: ') + SwT(SelectedDataRoot) + NewLine + NewLine + DataSummary;
    exit;
  end;
  Result := SwT(InstallAction) + ' SayWhat? {#Version}' + NewLine + DetectionText + NewLine + NewLine +
    SwT('Install folder: ') + WizardDirValue + NewLine + SwT('App data folder: ') + SwT(SelectedDataRoot) + NewLine + NewLine +
    DataSummary;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then begin
    ReadInstallAction;
    if UninstallFromSetup then begin
      WizardForm.NextButton.Caption := SwT('&Uninstall');
      WizardForm.ReadyLabel.Caption := SwT('Ready to uninstall SayWhat? No replacement app will be installed. Review the data choices below.');
      exit;
    end;
    WizardForm.NextButton.Caption := '&' + SwT(InstallAction);
    WizardForm.ReadyLabel.Caption := SwT('Ready to ') + LowerCase(SwT(InstallAction)) + SwT(' SayWhat? Review the version and data choices below.');
  end else if CurPageID = wpFinished then begin
    if DataActionFailure <> '' then begin
      WizardForm.FinishedHeadingLabel.Caption := SwT('App installed; data changes need attention');
      WizardForm.FinishedLabel.Caption := DataActionFailure;
    end else begin
      if InstallAction = 'Update' then WizardForm.FinishedHeadingLabel.Caption := SwT('SayWhat? updated')
      else if InstallAction = 'Repair' then WizardForm.FinishedHeadingLabel.Caption := SwT('SayWhat? repaired')
      else WizardForm.FinishedHeadingLabel.Caption := SwT('SayWhat? installed');
      WizardForm.FinishedLabel.Caption := SwT('Version {#Version} is ready.') + #13#10#13#10 + DataActionMessage + #13#10#13#10 +
        SwT('Open SayWhat from Start when you are ready. Installation has not started translation.');
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  ReadInstallAction;
  Result := UninstallFromSetup and ((PageID = wpLicense) or (PageID = wpInfoBefore) or
    (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectTasks));
  if AutoUpdateRequested then
    Result := (PageID = wpWelcome) or (PageID = InstallDetectionPage.ID) or
      (PageID = InstallActionPage.ID) or (PageID = InstallDataPage.ID) or
      (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectTasks);
  if AutoUpdateRequested and WizardSilent then
    Result := Result or (PageID = wpLicense) or (PageID = wpInfoBefore);
  // Verified in-app updates already have explicit consent in the application;
  // native /SILENT handles the remaining steps with visible progress and errors.
  // Fresh/manual setup keeps its license and notices. Shutdown, scope, identity
  // and data-safety checks still run; no force-close or automatic Windows restart.
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if NestedUninstallFinished then begin Cancel := True; Confirm := False; end;
end;

function VerifyDataHelper(const Path: String): Boolean;
begin
  Result := FileExists(Path) and SameText(GetSHA256OfFile(Path), ExpectedDataActionHash);
end;

function RunDataHelper(ValidateOnly: Boolean): String;
var Arguments, Message, Details, Nonce, GrantedSid, SourceHelper: String;
  ResultCode, Handle, TempHandle: Integer; Started: Boolean;
begin
  Result := '';
  EnsureLifetimeReceiptPin;
  TempHandle := PinReceiptDirectory(ExpandConstant('{tmp}'));
  try
  if IsUninstaller then begin
    if ValidateOnly then begin
      SourceHelper := ExpandConstant('{app}\provisioning\manage-install-data.ps1');
      if not VerifyDataHelper(SourceHelper) then begin
        Result := SwT('The installed data-safety helper is missing or changed. Reinstall this version to repair it before uninstalling. No files were removed.'); exit;
      end;
      DataHelperPath := ExpandConstant('{tmp}\saywhat-uninstall-data.ps1');
      if not CopyFile(SourceHelper, DataHelperPath, True) then begin
        Result := SwT('The verified uninstall helper could not be prepared. No files were removed.'); exit;
      end;
    end;
  end else begin
    ExtractTemporaryFile('manage-install-data.ps1');
    DataHelperPath := ExpandConstant('{tmp}\manage-install-data.ps1');
  end;
  if not VerifyDataHelper(DataHelperPath) then begin Result := SwT('The data-safety helper did not match this installer. No optional data changes were requested.'); exit; end;
  DataReceiptPath := ExpandConstant('{tmp}\saywhat-data-actions.ini');
  Nonce := ''; GrantedSid := '';
  if DataExecAsOriginalUser then begin
    Nonce := NewReceiptNonce;
    if not PrepareOriginalReceipt(DataReceiptPath, Nonce, GrantedSid) then begin
      Result := SwT('The protected data-safety receipt could not be prepared. No files were changed.'); exit;
    end;
    if SupportedDataRoot and not SameText(GrantedSid, PrivateUserSid) then begin
      Result := SwT('The verified Windows account changed. All personal data was kept.'); exit;
    end;
  end;
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(DataHelperPath) +
    ' -DataRoot ';
  if ValidateOnly then Arguments := Arguments + AddQuotes(PreflightDataRoot) + ' -ValidateOnly'
  else Arguments := Arguments + AddQuotes(SelectedDataRoot);
  if ResetPreferencesChoice then Arguments := Arguments + ' -ResetPreferences';
  if ClearHistoryChoice then Arguments := Arguments + ' -ClearHistory';
  if DeleteModelsChoice then Arguments := Arguments + ' -DeleteCachedModels';
  if SupportedDataRoot then Arguments := Arguments + ' -RequirePrivateUserRoot';
  if DataExecAsOriginalUser then begin
    Arguments := Arguments + ' -RequireProtectedReceipt -ExpectedUserSid ' + GrantedSid + ' -ReceiptNonce ' + Nonce;
    if SupportedDataRoot then Arguments := Arguments + ' -RequireOriginalUserToken';
  end else if SupportedDataRoot then Arguments := Arguments + ' -ExpectedUserSid ' + AddQuotes(PrivateUserSid);
  Arguments := Arguments + ' -ReceiptPath ' + AddQuotes(DataReceiptPath);
  // Apply and preflight use the same account that supplied the displayed SID.
  // The helper independently rechecks it before any selected file can change.
  if DataExecAsOriginalUser then
    Started := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else Started := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Started then begin
    Result := SwT('The data-safety check could not start: ') + SysErrorMessage(ResultCode);
    exit;
  end;
  Handle := LockReceipt(DataReceiptPath);
  try
  if DataExecAsOriginalUser and not SameText(GetIniString('Result', 'ReceiptNonce', '', DataReceiptPath), Nonce) then begin
    Result := SwT('The protected data-safety receipt did not match this request. All personal data was kept.'); exit;
  end;
  Message := GetIniString('Result', 'Message', SwT('The data-safety helper did not return a receipt.'), DataReceiptPath);
  Details := GetIniString('Result', 'Details', '', DataReceiptPath);
  // Only exact known helper summaries translate; paths and diagnostic errors
  // are not catalog keys and remain untouched.
  Message := SwT(Message);
  Details := SwT(Details);
  if (ResultCode <> 0) or (GetIniString('Result', 'Success', '0', DataReceiptPath) <> '1') then begin
    Result := Message;
    if Details <> '' then Result := Result + #13#10 + Details;
    Result := Result + #13#10 + SwT('Receipt: ') + DataReceiptPath;
    Result := Result + #13#10 + SwT('Helper exit code: ') + IntToStr(ResultCode);
    Log('Data-safety helper failed: ' + Result);
  end else if not ValidateOnly then begin
    DataActionMessage := Message;
    if Details <> '' then DataActionMessage := DataActionMessage + #13#10 + Details;
    if GetIniString('Result', 'BackupPath', '', DataReceiptPath) <> '' then
      DataActionMessage := DataActionMessage + #13#10 + SwT('Backup: ') + GetIniString('Result', 'BackupPath', '', DataReceiptPath);
  end;
  finally ReceiptCloseHandle(Handle); end;
  finally ReceiptCloseHandle(TempHandle); end;
end;

function DataFlowGetFileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function DataFlowGetLastError: Cardinal;
  external 'GetLastError@kernel32.dll stdcall';

function IsProtectedShutdownTarget(const AppDir, Exe: String): Boolean;
var Folder, ProgramFiles64, ProgramFiles32, Ancestor, Parent: String; Attributes: Cardinal;
begin
  Result := False;
  Folder := AddBackslash(ExpandFileName(AppDir));
  ProgramFiles64 := AddBackslash(ExpandConstant('{pf64}'));
  ProgramFiles32 := AddBackslash(ExpandConstant('{pf32}'));
  if not SameText(Copy(Folder, 1, Length(ProgramFiles64)), ProgramFiles64) and
    not SameText(Copy(Folder, 1, Length(ProgramFiles32)), ProgramFiles32) then exit;
  Ancestor := Exe;
  while Ancestor <> '' do begin
    Attributes := DataFlowGetFileAttributes(Ancestor);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then exit;
    Parent := ExtractFileDir(RemoveBackslashUnlessRoot(Ancestor));
    if (Parent = '') or PathSame(Parent, Ancestor) then break;
    Ancestor := Parent;
  end;
  Result := True;
end;

function RequestRegisteredAppQuit(const AppDir, Exe, Scope: String): String;
var Version, Arguments: String; InstalledPacked, SupportedPacked: Int64;
  ResultCode: Integer; Started: Boolean;
begin
  Result := '';
  // Never start a portable/unregistered app or guess another executable. Only
  // this registered installation's new, noninteractive control entry point is
  // eligible. Legacy versions must be quit manually; their startup arguments
  // must not accidentally launch a dashboard or translation tools.
  if not IsSafeLocalFolder(AppDir) or not PathSame(ExtractFileDir(Exe), AppDir) or
    not (SameText(ExtractFileName(Exe), 'SayWhat.exe') or SameText(ExtractFileName(Exe), 'LocalTalk.exe')) then begin
    Result := SwT('The selected installation could not be identified safely. Close SayWhat? manually, then retry setup. No files were changed.');
    exit;
  end;
  if not FileExists(Exe) then begin
    if CheckForMutexes('Local\LocalTalkDesktop') then
      Result := SwT('SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.');
    exit;
  end;
  // ExecAsOriginalUser can fall back to the current elevated token when setup
  // itself was started elevated. Never promote a personal/custom writable app.
  // If it is already closed, the independent metadata preflight can continue.
  if IsAdmin and ((Scope <> 'All users') or not IsProtectedShutdownTarget(AppDir, Exe)) then begin
    if CheckForMutexes('Local\LocalTalkDesktop') then
      Result := SwT('SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.');
    exit;
  end;
  if not GetVersionNumbersString(Exe, Version) or not StrToVersion(Version, InstalledPacked) or
    not StrToVersion('0.1.11', SupportedPacked) then begin
    Result := SwT('The selected installation could not be identified safely. Close SayWhat? manually, then retry setup. No files were changed.');
    exit;
  end;
  if ComparePackedVersion(InstalledPacked, SupportedPacked) < 0 then begin
    if CheckForMutexes('Local\LocalTalkDesktop') then
      Result := SwT('SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.');
    exit;
  end;
  Arguments := '--installer-shutdown --installation-path ' + AddQuotes(ExpandFileName(Exe)) + ' --timeout-seconds 75';
  // Prefer the original account for a protected all-users executable. The pipe
  // still validates its owner/session; another account or session fails closed.
  if IsAdmin and not IsUninstaller then
    Started := ExecAsOriginalUser(Exe, Arguments, AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else Started := Exec(Exe, Arguments, AppDir, SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Started then begin
    Result := SwT('The app could not be asked to close safely. Quit SayWhat? from its tray menu, then retry setup. No files were changed.');
    exit;
  end;
  if ResultCode <> 0 then begin
    if ResultCode = 25 then
      Result := SwT('This installation is open in another Windows account or session. Ask that user to quit SayWhat?, then retry setup. No files were changed.')
    else if ResultCode = 23 then
      Result := SwT('SayWhat? has not finished closing. Wait for translation tools to stop, or choose Retry quit in the app, then retry setup. No files were changed.')
    else Result := SwT('The app did not finish a verified shutdown. Quit SayWhat? manually, then retry setup. No files were changed.');
    Result := Result + #13#10 + SwT('Shutdown check code: ') + IntToStr(ResultCode);
    exit;
  end;
  // Retain the old single-instance guard for unsupported/other copies. The
  // data-safety helper below additionally checks all-session process metadata.
  if CheckForMutexes('Local\LocalTalkDesktop') then
    Result := SwT('SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.');
end;

function InstallDataPreflight: String;
begin
  Result := '';
  if AutoUpdateRequested then begin
    // The wizard can wait at license/Ready while another setup changes the
    // registration. Re-read it before shutdown or file replacement; never use
    // a cached Update decision to overwrite a newer or unregistered copy.
    DetectInstallState;
    if not IsVerifiedAppUpdate or not PathSame(WizardDirValue, ExistingAppDir) then begin
      Result := SwT('The selected installation could not be identified safely. Close SayWhat? manually, then retry setup. No files were changed.');
      exit;
    end;
  end;
  ReadInstallAction;
  if UninstallFromSetup then begin Result := SwT('Uninstall must finish from the Ready page; no app installation was started.'); exit; end;
  if ScopeMismatch then begin Result := SwT('Choose the existing installation scope before updating. A conflicting second copy will not be installed.'); exit; end;
  ReadInstallDataChoices;
  if IsUnregisteredProgramInTarget then begin Result := SwT('An unregistered executable is in the target folder. Select a different installation folder.'); exit; end;
  if HasDataChoices and (ConfirmationSignature <> ConsentSignature) then begin
    Result := SwT('Return to the Ready page to confirm the optional data changes.');
    exit;
  end;
  if HasUserRecord and (ExistingAppDir <> '') then begin
    WizardForm.StatusLabel.Caption := SwT('Closing SayWhat? safely. Waiting for translation tools and models to stop…');
    Result := RequestRegisteredAppQuit(ExistingAppDir, ExistingExe, ExistingScope);
    if Result <> '' then exit;
  end else if CheckForMutexes('Local\LocalTalkDesktop') then begin
    Result := SwT('SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.');
    exit;
  end;
  try
    Result := RunDataHelper(True);
  except Result := SwT('The data-safety check could not complete: ') + GetExceptionMessage; end;
end;

function RegisteredAppIsGone(const AppDir, Scope: String): Boolean;
begin
  Result := not FileExists(AddBackslash(AppDir) + 'SayWhat.exe') and not FileExists(AddBackslash(AppDir) + 'LocalTalk.exe');
  if Scope = 'All users' then Result := Result and not RegKeyExists(HKLM32, AppUninstallKey) and not RegKeyExists(HKLM64, AppUninstallKey)
  else Result := Result and not RegKeyExists(HKCU32, AppUninstallKey) and not RegKeyExists(HKCU64, AppUninstallKey);
end;

function NoProgramFilesRemainIn(const Folder, Uninstaller: String; Depth: Integer; var Entries: Integer): Boolean;
var FindRec: TFindRec; Path, Ancestor, Parent: String; Attributes, LastError: Cardinal;
begin
  Result := False;
  if Depth > 16 then exit;
  Ancestor := Folder;
  // Refuse links in the app directory or its ancestors. Never inspect a linked
  // target, another model library or an unrelated user's data directory.
  while Ancestor <> '' do begin
    Attributes := DataFlowGetFileAttributes(Ancestor);
    if Attributes = $FFFFFFFF then begin
      LastError := DataFlowGetLastError;
      if (LastError = 2) or (LastError = 3) then begin Result := True; exit; end;
      exit;
    end;
    if (Attributes and $400) <> 0 then exit;
    Parent := ExtractFileDir(RemoveBackslashUnlessRoot(Ancestor));
    if (Parent = '') or PathSame(Parent, Ancestor) then break;
    Ancestor := Parent;
  end;
  if not DirExists(Folder) then exit;
  if not FindFirst(AddBackslash(Folder) + '*', FindRec) then exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
        Entries := Entries + 1;
        if Entries > 4096 then exit;
        Path := AddBackslash(Folder) + FindRec.Name;
        if (FindRec.Attributes and $400) <> 0 then exit;
        if (FindRec.Attributes and $10) <> 0 then begin
          if not NoProgramFilesRemainIn(Path, Uninstaller, Depth + 1, Entries) then exit;
        end else if not PathSame(Path, Uninstaller) and
          not PathSame(Path, ChangeFileExt(Uninstaller, '.dat')) and
          not PathSame(Path, ChangeFileExt(Uninstaller, '.msg')) then exit;
      end;
    until not FindNext(FindRec);
  finally FindClose(FindRec); end;
  Result := True;
end;

function NoProgramFilesRemain(const Folder, Uninstaller: String): Boolean;
var Entries: Integer;
begin
  Entries := 0;
  // An offline/unmounted program drive is not proof that its app was removed.
  Result := False;
  if not DirExists(AddBackslash(ExtractFileDrive(Folder))) then exit;
  Result := NoProgramFilesRemainIn(Folder, Uninstaller, 0, Entries);
end;

function WaitForRegisteredAppRemoval: Boolean;
var Attempt: Integer;
begin
  // The initial uninstaller process can exit before its second phase. Its exit
  // code alone is never sufficient evidence to clear personal data.
  Result := False;
  for Attempt := 0 to 199 do begin
    if RegisteredAppIsGone(ExistingAppDir, ExistingScope) and NoProgramFilesRemain(ExistingAppDir, ExistingUninstaller) then
      begin Result := True; exit; end;
    Sleep(100);
  end;
end;

function PerformSetupUninstall: Boolean;
var Error, Arguments, LogPath: String; ResultCode: Integer; Started: Boolean;
begin
  Result := False;
  if WizardSilent or not RegisteredUninstallAvailable then exit;
  // A detected hash proves unchanged, not trusted provenance. Never elevate a
  // per-user/custom writable uninstaller, including original-user fallback.
  if (IsAdmin and (ExistingScope <> 'All users')) or
    ((IsAdmin or (ExistingScope = 'All users')) and not IsProtectedShutdownTarget(ExistingAppDir, ExistingUninstaller)) then begin
    MsgBox(SwT('For a personal or custom installation, start its uninstaller normally from your own Windows account. Setup will not run a writable uninstaller with administrator privileges.'), mbError, MB_OK);
    exit;
  end;
  if MsgBox(SwT('Uninstall the registered SayWhat? app?') + #13#10#13#10 + SwT('Version: ') + ExistingVersion + #13#10 +
    SwT('Folder: ') + ExistingAppDir + #13#10 + SwT('Scope: ') + SwT(ExistingScope) + #13#10#13#10 +
    DataSummary + #13#10#13#10 + SwT('Selected data changes run only after the app is removed successfully. Continue?'),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then exit;
  WizardForm.NextButton.Enabled := False;
  WizardForm.BackButton.Enabled := False;
  WizardForm.CancelButton.Enabled := False;
  try
    try
      if not PathSame(ReadRegisteredUninstaller, ExistingUninstaller) or
        not SameText(GetSHA256OfFile(ExistingUninstaller), UninstallerHashAtDetection) then begin
        MsgBox(SwT('The registered uninstaller changed while setup was open. Close setup and inspect the installation before retrying. No app or data was removed.'), mbError, MB_OK);
        exit;
      end;
      WizardForm.ReadyLabel.Caption := SwT('Closing SayWhat? safely. Waiting for translation tools and models to stop…');
      Error := RequestRegisteredAppQuit(ExistingAppDir, ExistingExe, ExistingScope);
      if Error <> '' then begin MsgBox(Error, mbError, MB_OK); exit; end;
      Error := RunDataHelper(True);
      if Error <> '' then begin MsgBox(SwT('Uninstall was stopped before removing the app.') + #13#10#13#10 + Error, mbError, MB_OK); exit; end;
      LogPath := ExpandConstant('{tmp}\saywhat-registered-uninstall.log');
      // Never execute registry command-line arguments. The child is silent
      // keep-only; this visible wizard owns the single optional cleanup consent.
      Arguments := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=' + AddQuotes(LogPath);
      WizardForm.ReadyLabel.Caption := SwT('Removing the registered app. Optional data changes have not been applied yet.');
      if (ExistingScope = 'Current user') and IsAdminInstallMode then
        Started := ExecAsOriginalUser(ExistingUninstaller, Arguments, ExistingAppDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
      else if (ExistingScope = 'All users') and not IsAdminInstallMode then
        Started := ShellExec('runas', ExistingUninstaller, Arguments, ExistingAppDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
      else Started := Exec(ExistingUninstaller, Arguments, ExistingAppDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
      if not Started or (ResultCode <> 0) then begin
        MsgBox(SwT('The registered uninstaller did not finish successfully. No optional data cleanup was run.') + #13#10 +
          SwT('Code: ') + IntToStr(ResultCode) + #13#10 + SwT('Uninstall log: ') + LogPath, mbError, MB_OK);
        exit;
      end;
      if not WaitForRegisteredAppRemoval then begin
        MsgBox(SwT('Uninstall returned success, but app files or the uninstall record remain. No optional data cleanup was run.') + #13#10 +
          SwT('Uninstall log: ') + LogPath, mbError, MB_OK);
        exit;
      end;
      DataActionMessage := SwT('Preferences, saved people, logs and cached models were kept.');
      if HasDataChoices then begin
        Error := RunDataHelper(False);
        if Error <> '' then begin
          DataActionFailure := SwT('The app was removed, but optional data cleanup did not finish. Some selected files may remain.') + #13#10#13#10 + Error;
          Log(DataActionFailure);
          MsgBox(DataActionFailure, mbError, MB_OK);
          Result := True;
          exit;
        end;
      end;
      MsgBox(SwT('SayWhat? was uninstalled.') + #13#10#13#10 + DataActionMessage, mbInformation, MB_OK);
      Result := True;
    except MsgBox(SwT('Uninstall could not finish. Review the app and receipt before retrying: ') + GetExceptionMessage, mbError, MB_OK); end;
  finally
    WizardForm.NextButton.Enabled := True;
    WizardForm.BackButton.Enabled := True;
    WizardForm.CancelButton.Enabled := True;
  end;
end;

<event('CurStepChanged')>
procedure ApplyInstallDataChoices(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or PostDataApplied then exit;
  PostDataApplied := True;
  DataActionMessage := SwT('Your preferences, saved people, logs and cached models were kept.');
  if not HasDataChoices then exit;
  try DataActionFailure := RunDataHelper(False);
  except DataActionFailure := GetExceptionMessage; end;
  if DataActionFailure <> '' then begin
    DataActionFailure := SwT('The app was installed, but optional data cleanup did not finish. Some selected files may remain.') + #13#10#13#10 +
      DataActionFailure;
    SuppressibleMsgBox(DataActionFailure, mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if DataActionFailure <> '' then Result := 10;
end;

function ShowUninstallDataChoices: Boolean;
var Form: TSetupForm; Heading, Explanation: TNewStaticText;
  ResetBox, HistoryBox, ModelsBox: TNewCheckBox; RemoveButton, CancelButton: TNewButton;
  CanChangeData: Boolean;
begin
  Result := False;
  // Uninstall has no original-user token to delegate to. Personal data is
  // user-writable, so never mutate it with this elevated process's authority.
  // Normal Setup still uses its verified original-user helper path unchanged.
  CanChangeData := SupportedDataRoot and not IsAdmin;
  Form := CreateCustomForm(560, 430, False, False);
  try
    Form.Caption := SwT('Uninstall SayWhat?');
    Heading := TNewStaticText.Create(Form);
    Heading.Parent := Form;
    Heading.Left := ScaleX(20); Heading.Top := ScaleY(18); Heading.Width := ScaleX(520);
    Heading.Height := ScaleY(40); Heading.AutoSize := False; Heading.WordWrap := True;
    Heading.Caption := SwT('Remove the app. Keep your data unless you explicitly select a change below.');
    ResetBox := TNewCheckBox.Create(Form); ResetBox.Parent := Form;
    ResetBox.Left := ScaleX(20); ResetBox.Top := ScaleY(74); ResetBox.Width := ScaleX(520);
    ResetBox.Height := ScaleY(22);
    ResetBox.Caption := SwT('Reset app preferences'); ResetBox.Checked := False;
    HistoryBox := TNewCheckBox.Create(Form); HistoryBox.Parent := Form;
    HistoryBox.Left := ScaleX(20); HistoryBox.Top := ScaleY(108); HistoryBox.Width := ScaleX(520);
    HistoryBox.Height := ScaleY(22);
    HistoryBox.Caption := SwT('Remove saved people and translation log history'); HistoryBox.Checked := False;
    ModelsBox := TNewCheckBox.Create(Form); ModelsBox.Parent := Form;
    ModelsBox.Left := ScaleX(20); ModelsBox.Top := ScaleY(142); ModelsBox.Width := ScaleX(520);
    ModelsBox.Height := ScaleY(22);
    ModelsBox.Caption := SwT('Delete downloaded model files in the app cache'); ModelsBox.Checked := False;
    ResetBox.Enabled := CanChangeData; HistoryBox.Enabled := CanChangeData; ModelsBox.Enabled := CanChangeData;
    Explanation := TNewStaticText.Create(Form); Explanation.Parent := Form;
    Explanation.Left := ScaleX(20); Explanation.Top := ScaleY(184); Explanation.Width := ScaleX(520);
    Explanation.Height := ScaleY(175); Explanation.AutoSize := False; Explanation.WordWrap := True;
    if IsAdmin then
      Explanation.Caption := SwT('Your settings, saved people and downloaded models will be kept. This uninstaller cannot safely clear personal data while running with administrator permissions.')
    else Explanation.Caption := DataPageWarning;
    RemoveButton := TNewButton.Create(Form); RemoveButton.Parent := Form;
    RemoveButton.Left := ScaleX(328); RemoveButton.Top := ScaleY(380); RemoveButton.Width := ScaleX(100);
    RemoveButton.Caption := SwT('Uninstall'); RemoveButton.ModalResult := mrOk; RemoveButton.Default := False;
    CancelButton := TNewButton.Create(Form); CancelButton.Parent := Form;
    CancelButton.Left := ScaleX(440); CancelButton.Top := ScaleY(380); CancelButton.Width := ScaleX(100);
    CancelButton.Caption := SwT('Cancel'); CancelButton.ModalResult := mrCancel; CancelButton.Cancel := True; CancelButton.Default := True;
    Form.ActiveControl := CancelButton;
    if Form.ShowModal <> mrOk then exit;
    ResetPreferencesChoice := CanChangeData and ResetBox.Checked;
    ClearHistoryChoice := CanChangeData and HistoryBox.Checked;
    DeleteModelsChoice := CanChangeData and ModelsBox.Checked;
    Result := ConfirmDataChoices(True);
  finally Form.Free; end;
end;

function InitializeUninstall: Boolean;
var SourceHelper, Exe, Scope: String; Error: String;
begin
  Result := False;
  ResolveInstallDataRoot;
  ResetPreferencesChoice := False; ClearHistoryChoice := False; DeleteModelsChoice := False;
  if not UninstallSilent then if not ShowUninstallDataChoices then exit;
  try
    Exe := ExpandConstant('{app}\SayWhat.exe');
    if not FileExists(Exe) then Exe := ExpandConstant('{app}\LocalTalk.exe');
    if IsAdminInstallMode then Scope := 'All users' else Scope := 'Current user';
    Error := RequestRegisteredAppQuit(ExpandConstant('{app}'), Exe, Scope);
    if Error <> '' then begin SuppressibleMsgBox(Error, mbError, MB_OK, IDOK); exit; end;
    // RunDataHelper validates/copies under its retained no-delete directory pin
    // BEFORE any application files are removed. Post-removal keeps that copy.
    Error := RunDataHelper(True);
    if Error <> '' then begin SuppressibleMsgBox(SwT('Uninstall was stopped before removing app files.') + #13#10#13#10 + Error, mbError, MB_OK, IDOK); exit; end;
    Result := True;
  except SuppressibleMsgBox(SwT('Uninstall was stopped before removing app files: ') + GetExceptionMessage, mbError, MB_OK, IDOK); end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Scope: String;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  if not HasDataChoices then begin
    Log('SayWhat? app removed. Preferences, saved people, logs and cached models retained.');
    exit;
  end;
  if IsAdminInstallMode then Scope := 'All users' else Scope := 'Current user';
  if not RegisteredAppIsGone(ExpandConstant('{app}'), Scope) or not NoProgramFilesRemain(ExpandConstant('{app}'), ExpandConstant('{uninstallexe}')) then
    RaiseException(SwT('App removal could not be verified: program files, extra files or an uninstall record remain. Personal data was kept; no optional cleanup was run.'));
  try DataActionFailure := RunDataHelper(False);
  except DataActionFailure := GetExceptionMessage; end;
  if DataActionFailure <> '' then
    RaiseException(SwT('The app files were removed, but optional data cleanup did not finish. Some selected files may remain.') + #13#10#13#10 + DataActionFailure);
  SuppressibleMsgBox(SwT('SayWhat? was removed.') + #13#10#13#10 + DataActionMessage, mbInformation, MB_OK, IDOK);
end;
