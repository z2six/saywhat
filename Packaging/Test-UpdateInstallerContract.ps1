<#
.SYNOPSIS
Read-only source checks for the verified, streamlined in-app update policy.
.DESCRIPTION
Reads only the current source tree. It never builds or executes setup, reads the
registry, touches user data, starts models, or changes a running application.
#>
[CmdletBinding()]
param([string]$SourceRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$updateContractRoot = [IO.Path]::GetFullPath($SourceRoot)
$updateLauncher = Get-Content -LiteralPath (Join-Path $updateContractRoot 'LocalTalk\AppUpdateInstaller.cs') -Raw
$updateInstaller = Get-Content -LiteralPath (Join-Path $updateContractRoot 'Packaging\SayWhat.iss') -Raw
$updateDataFlow = Get-Content -LiteralPath (Join-Path $updateContractRoot 'Packaging\InstallerDataFlow.iss') -Raw
$updateStrings = Get-Content -LiteralPath (Join-Path $updateContractRoot 'Packaging\InstallerStrings.iss') -Raw
$updateLanguage = Get-Content -LiteralPath (Join-Path $updateContractRoot 'Packaging\InstallerLanguage.iss') -Raw
$updateMainWindow = Get-Content -LiteralPath (Join-Path $updateContractRoot 'LocalTalk\MainWindow.cs') -Raw
$script:updateContractChecks = 0
function Assert-UpdateContract([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Update installer contract: $Description" }
    $script:updateContractChecks++
}
function Get-UpdateFunction([string]$Source, [string]$Name) {
    $updateFunctionPattern = '(?ms)^(?:function|procedure)\s+' + [regex]::Escape($Name) + '\b.*?(?=^(?:function|procedure)\s|\z)'
    $updateFunctionMatch = [regex]::Match($Source, $updateFunctionPattern)
    Assert-UpdateContract $updateFunctionMatch.Success "Function $Name is present"
    $updateFunctionMatch.Value
}

Assert-UpdateContract $updateLauncher.Contains('/AUTOUPDATE /SILENT /NORESTART ') 'verified update uses native visible progress and forbids automatic Windows restart'
foreach ($updateForbiddenFlag in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/FORCECLOSEAPPLICATIONS', '/NOCANCEL', '--start-all')) {
    Assert-UpdateContract (-not $updateLauncher.Contains($updateForbiddenFlag)) "Launcher does not add $updateForbiddenFlag"
}
Assert-UpdateContract ($updateLauncher.Contains('ReadInstallScope') -and $updateLauncher.Contains('/SAYWHATEXE=') -and
    $updateLauncher.Contains('/ALLUSERS') -and $updateLauncher.Contains('/CURRENTUSER') -and $updateLauncher.Contains('/LANG=')) 'launch passes the verified existing executable, scope and interface language'
Assert-UpdateContract ($updateLauncher.Contains('/SP- /LANG=') -and $updateLauncher.Contains('/DIR=') -and
    $updateLauncher.Contains('/SAYWHATTO=') -and $updateLauncher.Contains('Path.IsPathFullyQualified(currentExecutablePath)')) 'compact update avoids the startup prompt and pins an absolute install path and target version'
Assert-UpdateContract ($updateLauncher.Contains('FixedTimeEquals') -and $updateLauncher.Contains('VerifyExecutableHeaderAsync') -and
    $updateLauncher.Contains('FileShare.Read') -and $updateLauncher.Contains('download.Launch')) 'download capability retains size/hash/executable verification and write-denying lease'
Assert-UpdateContract ($updateLauncher.Contains('error.NativeErrorCode == 1223') -and
    $updateLauncher.Contains('throw new OperationCanceledException')) 'Windows permission refusal remains cancellation before owned shutdown'
$updateUiHandoff = [regex]::Match($updateMainWindow, '(?ms)^    private async Task InstallAvailableUpdateAsync\(\).*?(?=^    private\s|\z)').Value
Assert-UpdateContract ($updateUiHandoff.Contains('AppUpdateInstallerService.DownloadAsync') -and
    $updateUiHandoff.Contains('token.ThrowIfCancellationRequested();') -and $updateUiHandoff.Contains('AppUpdateInstallerService.LaunchInstaller') -and
    $updateUiHandoff.IndexOf('DownloadAsync') -lt $updateUiHandoff.IndexOf('LaunchInstaller')) 'Update now hands the verified downloaded capability directly to managed setup'
Assert-UpdateContract (-not $updateUiHandoff.Contains('RequestQuit') -and -not $updateUiHandoff.Contains('StopForTaskAsync') -and
    -not $updateUiHandoff.Contains('Environment.Exit') -and -not $updateUiHandoff.Contains('Close();')) 'dashboard does not close or discard its live session before setup permission succeeds'

$updateRunSection = [regex]::Match($updateInstaller, '(?ms)^\[Run\]\s*(.*?)^\[Code\]').Groups[1].Value
$updateRunLines = @($updateRunSection -split '\r?\n' | Where-Object { $_ -match '^Filename:' })
Assert-UpdateContract ($updateRunLines.Count -eq 2) 'normal optional launch and verified automatic update launch are separate entries'
$updateNormalRun = @($updateRunLines | Where-Object { $_ -match 'Check: CanLaunchSayWhatIdle$' })
$updateAutomaticRun = @($updateRunLines | Where-Object { $_ -match 'Check: CanRelaunchSayWhatAfterUpdate$' })
Assert-UpdateContract ($updateNormalRun.Count -eq 1 -and $updateNormalRun[0].Contains('postinstall nowait skipifsilent runasoriginaluser') -and
    -not $updateNormalRun[0].Contains('unchecked')) 'normal setup offers a checked, optional idle launch without starting models'
Assert-UpdateContract ($updateAutomaticRun.Count -eq 1 -and $updateAutomaticRun[0].Contains('nowait skipifnotsilent runasoriginaluser') -and
    -not $updateAutomaticRun[0].Contains('postinstall') -and -not $updateAutomaticRun[0].Contains('runhidden')) 'verified update can automatically launch idle UI after successful installation, without hidden/elevated UI'
foreach ($updateRunLine in $updateRunLines) {
    Assert-UpdateContract ($updateRunLine.Contains('--installer-launch-ui --ui-language') -and -not $updateRunLine.Contains('--start-all')) 'every relaunch uses the narrow no-model idle UI broker'
}
$updateRelaunchGuard = Get-UpdateFunction $updateInstaller 'CanRelaunchSayWhatAfterUpdate'
foreach ($updateRelaunchRequirement in @('AutoUpdateRequested and WizardSilent and IsVerifiedAppUpdate', 'not UninstallFromSetup',
    'not NestedUninstallFinished', "(DataActionFailure = '')", 'not HasDataChoices', 'PathSame(ExistingAppDir',
    'PathSame(ExistingExe', 'IsAdminInstallMode and IsProtectedShutdownTarget')) {
    Assert-UpdateContract $updateRelaunchGuard.Contains($updateRelaunchRequirement) "automatic idle launch requires $updateRelaunchRequirement"
}
$updateInitialization = Get-UpdateFunction $updateDataFlow 'InitializeSetup'
Assert-UpdateContract ($updateInitialization.Contains('AutoUpdateRequested and not IsVerifiedAppUpdate') -and
    $updateInitialization.Contains('Result := False;')) 'unidentified or non-update handoffs fail closed before setup'
$updateIdentity = Get-UpdateFunction $updateDataFlow 'IsVerifiedAppUpdate'
Assert-UpdateContract ($updateIdentity.Contains('not AmbiguousRecord and HasVerifiedApp and not ScopeMismatch') -and
    $updateIdentity.Contains("(InstallAction = 'Update')") -and $updateIdentity.Contains('{param:SAYWHATEXE|}') -and
    $updateIdentity.Contains('{param:SAYWHATTO|}') -and $updateIdentity.Contains("SameText(RequestedTargetVersion, '{#Version}')")) 'update requires registered scope, path, forward version and matching requested target build'
$updatePreflight = Get-UpdateFunction $updateDataFlow 'InstallDataPreflight'
Assert-UpdateContract ($updatePreflight.Contains('DetectInstallState;') -and $updatePreflight.Contains('not IsVerifiedAppUpdate') -and
    $updatePreflight.Contains('RequestRegisteredAppQuit') -and $updatePreflight.Contains('RunDataHelper(True)')) 'installation rechecks identity, owned shutdown and private-data safety before replacing files'
$updateDataChoices = Get-UpdateFunction $updateDataFlow 'ReadInstallDataChoices'
Assert-UpdateContract ($updateDataChoices.Contains('ResetPreferencesChoice := False;') -and $updateDataChoices.Contains('ClearHistoryChoice := False;') -and
    $updateDataChoices.Contains('DeleteModelsChoice := False;') -and $updateDataChoices.Contains('not AutoUpdateRequested and not WizardSilent')) 'automatic update cannot select destructive data changes'
$updateUninstallChoices = Get-UpdateFunction $updateDataFlow 'ShowUninstallDataChoices'
Assert-UpdateContract $updateUninstallChoices.Contains('CanChangeData := SupportedDataRoot and not IsAdmin;') 'only verified non-elevated uninstall can offer optional private-data changes'
foreach ($updateUninstallChoice in @('ResetBox.Enabled := CanChangeData', 'HistoryBox.Enabled := CanChangeData', 'ModelsBox.Enabled := CanChangeData',
    'ResetPreferencesChoice := CanChangeData and ResetBox.Checked', 'ClearHistoryChoice := CanChangeData and HistoryBox.Checked',
    'DeleteModelsChoice := CanChangeData and ModelsBox.Checked')) {
    Assert-UpdateContract $updateUninstallChoices.Contains($updateUninstallChoice) "uninstall cannot bypass the limited-token choice policy: $updateUninstallChoice"
}
$updateElevatedUninstallNotice = 'Your settings, saved people and downloaded models will be kept. This uninstaller cannot safely clear personal data while running with administrator permissions.'
Assert-UpdateContract ($updateUninstallChoices.Contains('if IsAdmin then') -and $updateUninstallChoices.Contains($updateElevatedUninstallNotice)) 'elevated uninstall explains that private data will be kept'
Assert-UpdateContract ($updateLanguage.Contains($updateElevatedUninstallNotice) -and $updateStrings.Contains($updateElevatedUninstallNotice)) 'elevated uninstall keep-data explanation is in the shared installer translation catalog'
$updatePrivateDataIdentity = Get-UpdateFunction $updateDataFlow 'ResolveInstallDataRoot'
Assert-UpdateContract $updatePrivateDataIdentity.Contains('DataExecAsOriginalUser := IsAdmin and not IsUninstaller;') 'elevated normal setup retains the verified original-user delegation path'
$updatePagePolicy = Get-UpdateFunction $updateDataFlow 'ShouldSkipPage'
Assert-UpdateContract ($updatePagePolicy.Contains('AutoUpdateRequested and WizardSilent') -and
    $updatePagePolicy.Contains('(PageID = wpLicense) or (PageID = wpInfoBefore)')) 'only the verified streamlined update path skips redundant license and notice pages'
Assert-UpdateContract ($updateInstaller.Contains('CloseApplications=no') -and $updateInstaller.Contains('RestartApplications=no')) 'native installer does not force-stop or restart arbitrary apps'
Assert-UpdateContract ($updateInstaller.Contains('not CanRelaunchSayWhatAfterUpdate') -and $updateLanguage.Contains("couldn''t open automatically") -and
    $updateStrings.Contains("couldn't open automatically")) 'a successful update gives a clear Start-menu fallback when secure idle relaunch is unavailable'
foreach ($updatePrivacyPhrase in @('Saved people and log history:', 'Remove saved people and translation log history',
    'Preferences, saved people, logs and models are kept by default.', 'Preferences, saved people, logs and cached models were kept.',
    'Your preferences, saved people, logs and cached models were kept.')) {
    Assert-UpdateContract ($updateDataFlow.Contains($updatePrivacyPhrase) -and $updateLanguage.Contains($updatePrivacyPhrase) -and
        $updateStrings.Contains($updatePrivacyPhrase)) "user-facing data terminology is aligned: $updatePrivacyPhrase"
}
foreach ($updateKey in @('Sw032', 'Sw044', 'Sw068', 'Sw072', 'Sw118', 'Sw122', 'Sw176', 'Sw177')) {
    foreach ($updateLocale in @('en', 'zh_CN', 'ja', 'ko')) {
        Assert-UpdateContract ([regex]::IsMatch($updateStrings, '(?m)^' + [regex]::Escape("$updateLocale.$updateKey=") + '.+$')) "data terminology is localized for $updateLocale/$updateKey"
    }
}
[pscustomobject]@{ Checks = $script:updateContractChecks; Policy = 'Verified update, visible progress, data kept, idle relaunch'; SourceRoot = $updateContractRoot }
