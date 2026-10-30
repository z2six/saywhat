#ifndef PayloadDir
  #error PayloadDir must point to the prepared installer payload.
#endif
#ifndef OutputDir
  #define OutputDir SourcePath + "dist"
#endif
#ifndef Version
  #define Version "0.2.14"
#endif
#define DataActionHash GetSHA256OfFile(PayloadDir + "\provisioning\manage-install-data.ps1")

[Setup]
AppId={{AB8C1B91-45FC-4F45-B457-420D736752D8}
; Owned app shutdown and the compatibility mutex are handled by DataFlow.
; AppMutex at startup would prevent the explicit graceful-quit interaction.
AppName=SayWhat?
AppVersion={#Version}
AppPublisher=SayWhat? contributors
AppComments=Unofficial local VRChat speech translation companion
DefaultDirName={autopf}\SayWhat
DefaultGroupName=SayWhat
; Keep the old installation directory on upgrade, but replace the visible group.
UsePreviousAppDir=yes
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809+ preview. Optional WSL/GPU speech and process-specific
; subtitles have separate runtime checks for their newer Windows requirements.
MinVersion=10.0.17763
WizardStyle=modern dark includetitlebar hidebevels
WizardBackColor=#191817
WizardImageBackColor=#191817
WizardSmallImageBackColor=#191817
WizardImageFile={#PayloadDir}\Assets\saywhat-icon-256.png
SetupIconFile={#PayloadDir}\Assets\SayWhat.ico
WizardSmallImageFile={#PayloadDir}\Assets\saywhat-icon-256.png
DisableWelcomePage=no
UninstallDisplayIcon={app}\SayWhat.exe
UninstallDisplayName=SayWhat?
LicenseFile={#PayloadDir}\licenses\GPL-3.0.txt
InfoBeforeFile={#PayloadDir}\INSTALLATION-NOTES.txt
OutputDir={#OutputDir}
OutputBaseFilename=SayWhat-{#Version}-Setup
Compression=lzma2/fast
SolidCompression=yes
LZMANumBlockThreads=1
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no
Uninstallable=yes
ShowLanguageDialog=yes
LanguageDetectionMethod=none
UsePreviousLanguage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "{#PayloadDir}\INSTALLATION-NOTES.txt"
Name: "zh_CN"; MessagesFile: "Languages\ChineseSimplified.isl"; InfoBeforeFile: "InstallerNotices\zh-CN.txt"
Name: "ja"; MessagesFile: "Languages\Japanese.isl"; InfoBeforeFile: "InstallerNotices\ja.txt"
Name: "ko"; MessagesFile: "Languages\Korean.isl"; InfoBeforeFile: "InstallerNotices\ko.txt"

#include "InstallerStrings.iss"

[Tasks]
Name: desktopicon; Description: "{cm:SwDesktopShortcut}"; GroupDescription: "{cm:SwShortcuts}"; Flags: unchecked
Name: microsoftvcruntime; Description: "{cm:SwMicrosoftRuntime}"; GroupDescription: "{cm:SwPrerequisite}"; Flags: unchecked; Check: NeedsVCRuntime

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\provisioning\verify-microsoft-signature.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\provisioning\manage-install-data.ps1"; Flags: dontcopy
; Logged program files only: this default never rewrites an existing user profile.
Source: "InstallerDefaults\en.json"; DestDir: "{app}"; DestName: "ui-language.json"; Languages: en; Flags: ignoreversion
Source: "InstallerDefaults\zh-CN.json"; DestDir: "{app}"; DestName: "ui-language.json"; Languages: zh_CN; Flags: ignoreversion
Source: "InstallerDefaults\ja.json"; DestDir: "{app}"; DestName: "ui-language.json"; Languages: ja; Flags: ignoreversion
Source: "InstallerDefaults\ko.json"; DestDir: "{app}"; DestName: "ui-language.json"; Languages: ko; Flags: ignoreversion

[Icons]
Name: "{group}\SayWhat"; Filename: "{app}\SayWhat.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:SwUninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SayWhat"; Filename: "{app}\SayWhat.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
; Exact obsolete program files/shortcuts only. Never delete app data or models.
Type: files; Name: "{app}\LocalTalk.exe"
Type: files; Name: "{app}\LocalTalk.dll"
Type: files; Name: "{app}\LocalTalk.pdb"
Type: files; Name: "{app}\LocalTalk.deps.json"
Type: files; Name: "{app}\LocalTalk.runtimeconfig.json"
Type: files; Name: "{app}\source\LocalTalk-Corresponding-Source.zip"
Type: files; Name: "{autodesktop}\LocalTalk.lnk"
Type: files; Name: "{autoprograms}\LocalTalk\LocalTalk.lnk"
Type: files; Name: "{autoprograms}\LocalTalk\Uninstall LocalTalk.lnk"
Type: dirifempty; Name: "{autoprograms}\LocalTalk"

[Run]
; Explicit idle launch only. The app safely rejects elevated/different-user fallback.
Filename: "{app}\SayWhat.exe"; Parameters: "--installer-launch-ui --ui-language {code:SayWhatUiLanguage}"; WorkingDir: "{app}"; Description: "{cm:SwLaunchIdle}"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: CanLaunchSayWhatIdle
; Only the exact verified in-app update reopens idle UI automatically. Normal
; setup keeps its optional checked idle launch above. Native /SILENT still displays
; progress and errors; /LANG and scope are supplied by the app. This entry can
; never pass --start-all or load a model, and is not processed on install failure
; or cancellation. The install preflight closes only this registered app after
; UAC succeeds, so cancelling the permission prompt leaves translation running.
Filename: "{app}\SayWhat.exe"; Parameters: "--installer-launch-ui --ui-language {code:SayWhatUiLanguage}"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent runasoriginaluser; Check: CanRelaunchSayWhatAfterUpdate

; No [UninstallDelete] section: downloaded models/preferences are retained.

[Code]
const
  VCRuntimeUrl = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe';
  VCRuntimeHash = '843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c';
  AppUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{AB8C1B91-45FC-4F45-B457-420D736752D8}_is1';
  ExpectedDataActionHash = '{#DataActionHash}';
var
  DownloadPage: TDownloadWizardPage;
  InstallDataPage: TInputOptionWizardPage;
  InstallActionPage: TInputOptionWizardPage;
  InstallDetectionPage: TOutputMsgMemoWizardPage;
  InstallDataWarning: TNewMemo;
  InstallAction, DetectionText, ExistingAppDir, ExistingVersion, ExistingExe: String;
  ExistingScope, SelectedScope, ExistingUninstaller, UninstallerHashAtDetection: String;
  SelectedDataRoot, PreflightDataRoot, DataHelperPath, DataReceiptPath: String;
  PrivateUserSid, PrivateUserAccount: String;
  DataActionMessage, DataActionFailure, ConfirmationSignature: String;
  HasUserRecord, HasVerifiedApp, MachineRecord, AmbiguousRecord: Boolean;
  ScopeMismatch, RegisteredUninstallAvailable, UninstallFromSetup, NestedUninstallFinished: Boolean;
  ExistingRegistryRoot: Integer;
  SupportedDataRoot, ResetPreferencesChoice, ClearHistoryChoice, DeleteModelsChoice: Boolean;
  PostDataApplied, DowngradeChoice: Boolean;
  AutoUpdateRequested: Boolean;
  PrivateUserVerified, DataExecAsOriginalUser: Boolean;
  ReceiptTempPinned: Boolean;
  LifetimeReceiptTempHandle: Integer;

#include "InstallerLanguage.iss"
#include "InstallerDataFlow.iss"

function CanLaunchSayWhatIdle: Boolean;
begin
  // Never enter an elevated personal/custom executable before the UI broker.
  Result := not WizardSilent and not UninstallFromSetup and
    not NestedUninstallFinished and (DataActionFailure = '');
  if Result and IsAdmin then
    Result := IsAdminInstallMode and IsProtectedShutdownTarget(
      ExpandConstant('{app}'), ExpandConstant('{app}\SayWhat.exe'));
end;

function CanRelaunchSayWhatAfterUpdate: Boolean;
begin
  Result := AutoUpdateRequested and WizardSilent and IsVerifiedAppUpdate and
    not UninstallFromSetup and not NestedUninstallFinished and
    (DataActionFailure = '') and not HasDataChoices and
    PathSame(ExistingAppDir, ExpandConstant('{app}')) and
    PathSame(ExistingExe, ExpandConstant('{app}\SayWhat.exe'));
  // runasoriginaluser normally supplies the pre-UAC token. If setup was started
  // elevated instead, only a protected all-users executable may reach the narrow
  // same-user idle UI broker; never promote a personal/custom writable app.
  if Result and IsAdmin then
    Result := IsAdminInstallMode and IsProtectedShutdownTarget(
      ExpandConstant('{app}'), ExpandConstant('{app}\SayWhat.exe'));
end;

function HasRuntimeRegistry(Root: Integer): Boolean;
var Installed, Major: Cardinal;
begin
  Result := RegQueryDWordValue(Root, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and
    RegQueryDWordValue(Root, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', Major);
  if Result then Result := (Installed = 1) and (Major >= 14);
end;

function NeedsVCRuntime: Boolean;
begin
  Result := not ((HasRuntimeRegistry(HKLM64) or HasRuntimeRegistry(HKLM32)) and
    FileExists(ExpandConstant('{sys}\msvcp140.dll')) and
    FileExists(ExpandConstant('{sys}\vcruntime140.dll')) and
    FileExists(ExpandConstant('{sys}\vcruntime140_1.dll')));
end;

procedure InitializeWizard;
begin
  InitializeInstallDataPage;
  InitializeSayWhatInstaller;
  DownloadPage := CreateDownloadPage(SwT('Microsoft prerequisite'),
    SwT('Downloading the Microsoft prerequisite. Windows will ask for administrator permission; no automatic restart.'), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Result := InstallDataPreflight;
  if Result <> '' then exit;
  if not NeedsVCRuntime or not WizardIsTaskSelected('microsoftvcruntime') then exit;
  try
    DownloadPage.Clear;
    DownloadPage.Add(VCRuntimeUrl, 'VC_redist.x64.exe', VCRuntimeHash);
    DownloadPage.Show;
    try
      DownloadPage.Download;
    finally
      DownloadPage.Hide;
    end;
    ExtractTemporaryFile('verify-microsoft-signature.ps1');
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\verify-microsoft-signature.ps1') +
      '" -Path "' + ExpandConstant('{tmp}\VC_redist.x64.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
      Result := SwT('Microsoft signature verification could not run: ') + SysErrorMessage(ResultCode); exit;
    end;
    if ResultCode <> 0 then begin
      Result := SwT('Microsoft signature verification failed; the prerequisite was not installed.'); exit;
    end;
    if not ShellExec('runas', ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /passive /norestart',
      '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then begin
      Result := SwT('Microsoft runtime setup was cancelled or could not start: ') + SysErrorMessage(ResultCode); exit;
    end;
    if ResultCode = 3010 then begin
      MsgBox(SwT('Microsoft runtime setup requested a restart. No automatic restart will occur. Restart Windows when convenient before using translation.'), mbInformation, MB_OK);
    end else if ResultCode <> 0 then begin
      Result := SwT('Microsoft runtime setup returned code ') + IntToStr(ResultCode) + SwT('. Review its Microsoft setup log or retry.'); exit;
    end;
    if NeedsVCRuntime then Result := SwT('The Microsoft runtime is still not available. Restart Windows if requested, then run SayWhat? setup again.');
  except
    Result := SwT('The Microsoft prerequisite could not be downloaded/verified: ') + GetExceptionMessage;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and AutoUpdateRequested and WizardSilent and
    not CanRelaunchSayWhatAfterUpdate and (DataActionFailure = '') then
    MsgBox(SwT('SayWhat? was updated, but couldn''t open automatically. Open it from the Start menu. No translation tools were started.'), mbInformation, MB_OK);
  if (CurStep = ssDone) and NeedsVCRuntime then
    MsgBox(SwT('The SayWhat? app files are ready. Open SayWhat and choose Set up SayWhat? to finish preparing translation. Windows may ask for permission.'), mbInformation, MB_OK);
end;
