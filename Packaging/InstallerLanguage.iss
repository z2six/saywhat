// Included before InstallerDataFlow.iss. Keep internal scope/action values in English;
// call SwT only at display sites. Unknown diagnostic text remains unchanged.
function SwT(const English: String): String;
begin
  Result := English;
  if English = 'Your settings, saved people and downloaded models will be kept. This uninstaller cannot safely clear personal data while running with administrator permissions.' then begin Result := CustomMessage('Sw177'); exit; end;
  if English = 'SayWhat? was updated, but couldn''t open automatically. Open it from the Start menu. No translation tools were started.' then begin Result := CustomMessage('Sw176'); exit; end;
  if English = 'The protected data-safety receipt could not be prepared. No files were changed.' then begin Result := CustomMessage('Sw170'); exit; end;
  if English = 'The protected data-safety receipt could not be read safely. No optional data changes were requested.' then begin Result := CustomMessage('Sw171'); exit; end;
  if English = 'The verified Windows account changed. All personal data was kept.' then begin Result := CustomMessage('Sw172'); exit; end;
  if English = 'The protected data-safety receipt did not match this request. All personal data was kept.' then begin Result := CustomMessage('Sw173'); exit; end;
  if English = 'The protected data-safety check has not completed.' then begin Result := CustomMessage('Sw174'); exit; end;
  if English = 'Helper exit code: ' then begin Result := CustomMessage('Sw175') + ' '; exit; end;
  if English = 'Unverified Windows account (data kept)' then begin Result := CustomMessage('Sw161'); exit; end;
  if English = 'Your Windows account could not be verified. All personal data is kept. Start setup normally from your own account to choose private-data changes.' then begin Result := CustomMessage('Sw162'); exit; end;
  if English = 'Only the verified account shown below is affected, even with an all-users installation. Other Windows users keep all their data.' then begin Result := CustomMessage('Sw163'); exit; end;
  if English = 'Windows account: ' then begin Result := CustomMessage('Sw164') + ' '; exit; end;
  if English = 'These three choices are independent. They affect only the verified Windows account, not every user of a shared installation. Leave them unchecked to keep everything.' then begin Result := CustomMessage('Sw165'); exit; end;
  if English = 'Only this verified account can change; every other Windows user keeps all data.' then begin Result := CustomMessage('Sw166'); exit; end;
  if English = 'For a personal or custom installation, start its uninstaller normally from your own Windows account. Setup will not run a writable uninstaller with administrator privileges.' then begin Result := CustomMessage('Sw167'); exit; end;
  if English = 'Other users, imported files and Windows speech support are kept. Deleted cached models need downloading again.' then begin Result := CustomMessage('Sw168'); exit; end;
  if English = 'Optional changes affect only the verified Windows account, even for all-users installs. Leave all boxes unchecked to keep all data.' then begin Result := CustomMessage('Sw169'); exit; end;
  if English = 'The selected data choices are ready. No data was changed.' then begin
    Result := CustomMessage('Sw153');
    exit;
  end;
  if English = 'Your existing data was kept. No selected cached file needed removal.' then begin
    Result := CustomMessage('Sw154');
    exit;
  end;
  if English = 'Your selected data choices were applied.' then begin
    Result := CustomMessage('Sw155');
    exit;
  end;
  if English = 'Some selected data could not be removed. App installation or removal is complete, but data cleanup needs attention.' then begin
    Result := CustomMessage('Sw156');
    exit;
  end;
  if English = 'Data cleanup did not finish. Some files are retained in the reported transaction folder.' then begin
    Result := CustomMessage('Sw157');
    exit;
  end;
  if English = 'Data cleanup did not run. Your existing data was kept.' then begin
    Result := CustomMessage('Sw158');
    exit;
  end;
  if English = 'Only selected, recognized app files were removed. Imported model files outside the app cache, speech support and runtime registrations were kept.' then begin
    Result := CustomMessage('Sw159');
    exit;
  end;
  if English = 'Only selected, recognized app files were removed. Imported model files outside the app cache, speech support and runtime registrations were kept. Model setup will be offered again; other preferences were kept.' then begin
    Result := CustomMessage('Sw160');
    exit;
  end;
  if English = 'Each Windows user profile (kept)' then begin
    Result := CustomMessage('Sw001');
    exit;
  end;
  if English = 'All users' then begin
    Result := CustomMessage('Sw002');
    exit;
  end;
  if English = 'Current user' then begin
    Result := CustomMessage('Sw003');
    exit;
  end;
  if English = 'Install' then begin
    Result := CustomMessage('Sw004');
    exit;
  end;
  if English = 'Update' then begin
    Result := CustomMessage('Sw005');
    exit;
  end;
  if English = 'Repair' then begin
    Result := CustomMessage('Sw006');
    exit;
  end;
  if English = 'Install older version' then begin
    Result := CustomMessage('Sw007');
    exit;
  end;
  if English = 'Uninstall' then begin
    Result := CustomMessage('Sw008');
    exit;
  end;
  if English = 'Cancel' then begin
    Result := CustomMessage('Sw009');
    exit;
  end;
  if English = 'Previous app files found without an uninstall registration:' then begin
    Result := CustomMessage('Sw010');
    exit;
  end;
  if English = ' (file version ' then begin
    Result := CustomMessage('Sw011');
    Result := ' ' + Result;
    Result := Result + ' ';
    exit;
  end;
  if English = 'They are not treated as an installed copy and will not be uninstalled or silently taken over.' then begin
    Result := CustomMessage('Sw012');
    exit;
  end;
  if English = 'Installed version: ' then begin
    Result := CustomMessage('Sw013');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Current installation: ' then begin
    Result := CustomMessage('Sw014');
    Result := Result + ' ';
    exit;
  end;
  if English = 'The same version is already installed. App files will be repaired.' then begin
    Result := CustomMessage('Sw015');
    exit;
  end;
  if English = 'A newer version is installed. This would replace it with an older version.' then begin
    Result := CustomMessage('Sw016');
    exit;
  end;
  if English = 'Your existing installation will be updated in place.' then begin
    Result := CustomMessage('Sw017');
    exit;
  end;
  if English = 'A previous installation is registered, but its app files are missing or cannot be verified.' then begin
    Result := CustomMessage('Sw018');
    exit;
  end;
  if English = 'Registered version: ' then begin
    Result := CustomMessage('Sw019');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Registered installation: ' then begin
    Result := CustomMessage('Sw020');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Setup will repair the registered app, not infer an update from an unrelated executable.' then begin
    Result := CustomMessage('Sw021');
    exit;
  end;
  if English = 'The registered version is newer. This repair installs an older version and requires confirmation.' then begin
    Result := CustomMessage('Sw022');
    exit;
  end;
  if English = 'No registered SayWhat? installation was found.' then begin
    Result := CustomMessage('Sw023');
    exit;
  end;
  if English = 'Saved app data is available at: ' then begin
    Result := CustomMessage('Sw024');
    Result := Result + ' ';
    exit;
  end;
  if English = 'This is a reinstall. Your saved data and cached models will be reused.' then begin
    Result := CustomMessage('Sw025');
    exit;
  end;
  if English = 'This is a new SayWhat? installation, version {#Version}.' then begin
    Result := CustomMessage('Sw026');
    exit;
  end;
  if English = 'Selected install scope: ' then begin
    Result := CustomMessage('Sw027');
    Result := Result + ' ';
    exit;
  end;
  if English = ' Existing scope: ' then begin
    Result := CustomMessage('Sw028');
    Result := ' ' + Result;
    Result := Result + ' ';
    exit;
  end;
  if English = 'To update, restart setup and choose the existing scope. Or uninstall the registered copy here first. A conflicting second installation will not be created.' then begin
    Result := CustomMessage('Sw029');
    exit;
  end;
  if English = 'Conflicting installation scopes, paths or identities are registered for SayWhat?.' then begin
    Result := CustomMessage('Sw030');
    exit;
  end;
  if English = 'Use Windows Installed apps to repair or remove the old entry before continuing. No app data has been changed.' then begin
    Result := CustomMessage('Sw031');
    exit;
  end;
  if English = 'All-users changes keep every Windows user''s settings, saved people, logs and cached models. Only shared app files change.' then begin
    Result := CustomMessage('Sw032');
    exit;
  end;
  if English = 'Elevated setup keeps personal data untouched. Run setup normally with Just me to choose private-data cleanup.' then begin
    Result := CustomMessage('Sw033');
    exit;
  end;
  if English = 'You use a custom data folder. Optional cleanup is disabled for safety; all data is kept.' then begin
    Result := CustomMessage('Sw034');
    exit;
  end;
  if English = 'Custom folder: ' then begin
    Result := CustomMessage('Sw035');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Keep everything: leave all three options unchecked.' then begin
    Result := CustomMessage('Sw036');
    exit;
  end;
  if English = 'Imported files outside the app cache and Windows speech support are kept. Deleted cached files must be downloaded again.' then begin
    Result := CustomMessage('Sw037');
    exit;
  end;
  if English = 'Data folder: ' then begin
    Result := CustomMessage('Sw038');
    Result := Result + ' ';
    exit;
  end;
  if English = 'All-users changes keep personal data untouched. These optional data choices are unavailable for shared installations.' then begin
    Result := CustomMessage('Sw039');
    exit;
  end;
  if English = 'These three choices are independent. They affect only this Windows account. Leave them unchecked to keep everything.' then begin
    Result := CustomMessage('Sw040');
    exit;
  end;
  if English = 'App preferences: ' then begin
    Result := CustomMessage('Sw041');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Reset' then begin
    Result := CustomMessage('Sw042');
    exit;
  end;
  if English = 'Keep' then begin
    Result := CustomMessage('Sw043');
    exit;
  end;
  if English = 'Saved people and log history: ' then begin
    Result := CustomMessage('Sw044');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Remove' then begin
    Result := CustomMessage('Sw045');
    exit;
  end;
  if English = 'Downloaded model files in the app cache: ' then begin
    Result := CustomMessage('Sw046');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Delete' then begin
    Result := CustomMessage('Sw047');
    exit;
  end;
  if English = 'Imported files outside the app cache and Windows speech support are kept.' then begin
    Result := CustomMessage('Sw048');
    exit;
  end;
  if English = 'All-users setup keeps every Windows user''s personal data.' then begin
    Result := CustomMessage('Sw049');
    exit;
  end;
  if English = 'Elevated setup keeps all personal data untouched.' then begin
    Result := CustomMessage('Sw050');
    exit;
  end;
  if English = 'Each Windows user keeps separate settings and models; data choices affect this account only.' then begin
    Result := CustomMessage('Sw051');
    exit;
  end;
  if English = 'Deleted cached models must be downloaded again. Setup will be required next time.' then begin
    Result := CustomMessage('Sw052');
    exit;
  end;
  if English = 'After uninstalling the app, apply these optional data changes?' then begin
    Result := CustomMessage('Sw053');
    exit;
  end;
  if English = 'After the app is installed successfully, apply these optional data changes?' then begin
    Result := CustomMessage('Sw054');
    exit;
  end;
  if English = 'Only the categories you selected will change. This can remove personal data and model downloads. Continue?' then begin
    Result := CustomMessage('Sw055');
    exit;
  end;
  if English = 'Your SayWhat? installation' then begin
    Result := CustomMessage('Sw056');
    exit;
  end;
  if English = 'Review the detected version, location and installation scope.' then begin
    Result := CustomMessage('Sw057');
    exit;
  end;
  if English = 'Only registered copies can be updated or uninstalled. This page does not change anything.' then begin
    Result := CustomMessage('Sw058');
    exit;
  end;
  if English = 'SayWhat? setup' then begin
    Result := CustomMessage('Sw059');
    exit;
  end;
  if English = 'Choose what you want to do.' then begin
    Result := CustomMessage('Sw060');
    exit;
  end;
  if English = 'Selected scope: ' then begin
    Result := CustomMessage('Sw061');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Updating keeps the existing app location. Uninstall removes the registered app without installing a replacement.' then begin
    Result := CustomMessage('Sw062');
    exit;
  end;
  if English = 'Uninstall the registered SayWhat? app' then begin
    Result := CustomMessage('Sw063');
    exit;
  end;
  if English = 'Keep or clear app data' then begin
    Result := CustomMessage('Sw064');
    exit;
  end;
  if English = 'Everything is kept unless you explicitly choose a change.' then begin
    Result := CustomMessage('Sw065');
    exit;
  end;
  if English = 'These three choices are independent. Just-me setup can change this account''s data; all-users setup keeps every user''s data.' then begin
    Result := CustomMessage('Sw066');
    exit;
  end;
  if English = 'Reset app preferences' then begin
    Result := CustomMessage('Sw067');
    exit;
  end;
  if English = 'Remove saved people and translation log history' then begin
    Result := CustomMessage('Sw068');
    exit;
  end;
  if English = 'Delete downloaded model files in the app cache' then begin
    Result := CustomMessage('Sw069');
    exit;
  end;
  if English = 'Selected installation scope: ' then begin
    Result := CustomMessage('Sw070');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Next, review detected app files and choose Install, Update, Repair or Uninstall.' then begin
    Result := CustomMessage('Sw071');
    exit;
  end;
  if English = 'Preferences, saved people, logs and models are kept by default.' then begin
    Result := CustomMessage('Sw072');
    exit;
  end;
  if English = 'An existing installation uses the other scope. Restart setup and choose ' then begin
    Result := CustomMessage('Sw073');
    Result := Result + ' ';
    exit;
  end;
  if English = ', or select Uninstall here. No conflicting second copy will be installed.' then begin
    Result := CustomMessage('Sw074');
    exit;
  end;
  if English = 'An app executable exists in the selected folder, but no matching SayWhat? installation is registered.' then begin
    Result := CustomMessage('Sw075');
    exit;
  end;
  if English = 'Choose a new empty installation folder, or repair the registration using the original installer. Setup will not take over an unregistered executable.' then begin
    Result := CustomMessage('Sw076');
    exit;
  end;
  if English = 'A newer version (' then begin
    Result := CustomMessage('Sw077');
    exit;
  end;
  if English = ') is installed. Replace it with {#Version}?' then begin
    Result := CustomMessage('Sw078');
    exit;
  end;
  if English = 'Older app versions may not understand saved settings from newer versions.' then begin
    Result := CustomMessage('Sw079');
    exit;
  end;
  if English = 'Uninstall SayWhat?' then begin
    Result := CustomMessage('Sw080');
    exit;
  end;
  if English = 'App folder: ' then begin
    Result := CustomMessage('Sw081');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Install scope: ' then begin
    Result := CustomMessage('Sw082');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Data folder for this account: ' then begin
    Result := CustomMessage('Sw083');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Install folder: ' then begin
    Result := CustomMessage('Sw084');
    Result := Result + ' ';
    exit;
  end;
  if English = 'App data folder: ' then begin
    Result := CustomMessage('Sw085');
    Result := Result + ' ';
    exit;
  end;
  if English = '&Uninstall' then begin
    Result := CustomMessage('Sw086');
    exit;
  end;
  if English = 'Ready to uninstall SayWhat? No replacement app will be installed. Review the data choices below.' then begin
    Result := CustomMessage('Sw087');
    exit;
  end;
  if English = 'Ready to ' then begin
    Result := CustomMessage('Sw088');
    Result := Result + ' ';
    exit;
  end;
  if English = ' SayWhat? Review the version and data choices below.' then begin
    Result := CustomMessage('Sw089');
    Result := ' ' + Result;
    exit;
  end;
  if English = 'App installed; data changes need attention' then begin
    Result := CustomMessage('Sw090');
    exit;
  end;
  if English = 'SayWhat? updated' then begin
    Result := CustomMessage('Sw091');
    exit;
  end;
  if English = 'SayWhat? repaired' then begin
    Result := CustomMessage('Sw092');
    exit;
  end;
  if English = 'SayWhat? installed' then begin
    Result := CustomMessage('Sw093');
    exit;
  end;
  if English = 'Version {#Version} is ready.' then begin
    Result := CustomMessage('Sw094');
    exit;
  end;
  if English = 'Open SayWhat from Start when you are ready. Installation has not started translation.' then begin
    Result := CustomMessage('Sw095');
    exit;
  end;
  if English = 'The data-safety helper did not match this installer. No optional data changes were requested.' then begin
    Result := CustomMessage('Sw096');
    exit;
  end;
  if English = 'The data-safety check could not start: ' then begin
    Result := CustomMessage('Sw097');
    Result := Result + ' ';
    exit;
  end;
  if English = 'The data-safety helper did not return a receipt.' then begin
    Result := CustomMessage('Sw098');
    exit;
  end;
  if English = 'Receipt: ' then begin
    Result := CustomMessage('Sw099');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Backup: ' then begin
    Result := CustomMessage('Sw100');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Uninstall must finish from the Ready page; no app installation was started.' then begin
    Result := CustomMessage('Sw101');
    exit;
  end;
  if English = 'Choose the existing installation scope before updating. A conflicting second copy will not be installed.' then begin
    Result := CustomMessage('Sw102');
    exit;
  end;
  if English = 'An unregistered executable is in the target folder. Select a different installation folder.' then begin
    Result := CustomMessage('Sw103');
    exit;
  end;
  if English = 'Return to the Ready page to confirm the optional data changes.' then begin
    Result := CustomMessage('Sw104');
    exit;
  end;
  if English = 'The data-safety check could not complete: ' then begin
    Result := CustomMessage('Sw105');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Uninstall the registered SayWhat? app?' then begin
    Result := CustomMessage('Sw106');
    exit;
  end;
  if English = 'Version: ' then begin
    Result := CustomMessage('Sw107');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Folder: ' then begin
    Result := CustomMessage('Sw108');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Scope: ' then begin
    Result := CustomMessage('Sw109');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Selected data changes run only after the app is removed successfully. Continue?' then begin
    Result := CustomMessage('Sw110');
    exit;
  end;
  if English = 'The registered uninstaller changed while setup was open. Close setup and inspect the installation before retrying. No app or data was removed.' then begin
    Result := CustomMessage('Sw111');
    exit;
  end;
  if English = 'Uninstall was stopped before removing the app.' then begin
    Result := CustomMessage('Sw112');
    exit;
  end;
  if English = 'Removing the registered app. Optional data changes have not been applied yet.' then begin
    Result := CustomMessage('Sw113');
    exit;
  end;
  if English = 'The registered uninstaller did not finish successfully. No optional data cleanup was run.' then begin
    Result := CustomMessage('Sw114');
    exit;
  end;
  if English = 'Code: ' then begin
    Result := CustomMessage('Sw115');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Uninstall log: ' then begin
    Result := CustomMessage('Sw116');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Uninstall returned success, but app files or the uninstall record remain. No optional data cleanup was run.' then begin
    Result := CustomMessage('Sw117');
    exit;
  end;
  if English = 'Preferences, saved people, logs and cached models were kept.' then begin
    Result := CustomMessage('Sw118');
    exit;
  end;
  if English = 'The app was removed, but optional data cleanup did not finish. Some selected files may remain.' then begin
    Result := CustomMessage('Sw119');
    exit;
  end;
  if English = 'SayWhat? was uninstalled.' then begin
    Result := CustomMessage('Sw120');
    exit;
  end;
  if English = 'Uninstall could not finish. Review the app and receipt before retrying: ' then begin
    Result := CustomMessage('Sw121');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Your preferences, saved people, logs and cached models were kept.' then begin
    Result := CustomMessage('Sw122');
    exit;
  end;
  if English = 'The app was installed, but optional data cleanup did not finish. Some selected files may remain.' then begin
    Result := CustomMessage('Sw123');
    exit;
  end;
  if English = 'Remove the app. Keep your data unless you explicitly select a change below.' then begin
    Result := CustomMessage('Sw124');
    exit;
  end;
  if English = 'All-users uninstall keeps every Windows user''s data. Only shared app files are removed.' then begin
    Result := CustomMessage('Sw125');
    exit;
  end;
  if English = 'Elevated uninstall keeps personal data untouched. Run setup normally with Just me for private-data cleanup.' then begin
    Result := CustomMessage('Sw126');
    exit;
  end;
  if English = 'The installed data-safety helper is missing or changed. Reinstall this version to repair it before uninstalling. No files were removed.' then begin
    Result := CustomMessage('Sw127');
    exit;
  end;
  if English = 'The verified uninstall helper could not be prepared. No files were removed.' then begin
    Result := CustomMessage('Sw128');
    exit;
  end;
  if English = 'Uninstall was stopped before removing app files.' then begin
    Result := CustomMessage('Sw129');
    exit;
  end;
  if English = 'Uninstall was stopped before removing app files: ' then begin
    Result := CustomMessage('Sw130');
    Result := Result + ' ';
    exit;
  end;
  if English = 'App removal could not be verified: program files, extra files or an uninstall record remain. Personal data was kept; no optional cleanup was run.' then begin
    Result := CustomMessage('Sw131');
    exit;
  end;
  if English = 'The app files were removed, but optional data cleanup did not finish. Some selected files may remain.' then begin
    Result := CustomMessage('Sw132');
    exit;
  end;
  if English = 'SayWhat? was removed.' then begin
    Result := CustomMessage('Sw133');
    exit;
  end;
  if English = 'Microsoft prerequisite' then begin
    Result := CustomMessage('Sw134');
    exit;
  end;
  if English = 'Downloading the Microsoft prerequisite. Windows will ask for administrator permission; no automatic restart.' then begin
    Result := CustomMessage('Sw135');
    exit;
  end;
  if English = 'Microsoft signature verification could not run: ' then begin
    Result := CustomMessage('Sw136');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Microsoft signature verification failed; the prerequisite was not installed.' then begin
    Result := CustomMessage('Sw137');
    exit;
  end;
  if English = 'Microsoft runtime setup was cancelled or could not start: ' then begin
    Result := CustomMessage('Sw138');
    Result := Result + ' ';
    exit;
  end;
  if English = 'Microsoft runtime setup requested a restart. No automatic restart will occur. Restart Windows when convenient before using translation.' then begin
    Result := CustomMessage('Sw139');
    exit;
  end;
  if English = 'Microsoft runtime setup returned code ' then begin
    Result := CustomMessage('Sw140');
    Result := Result + ' ';
    exit;
  end;
  if English = '. Review its Microsoft setup log or retry.' then begin
    Result := CustomMessage('Sw141');
    exit;
  end;
  if English = 'The Microsoft runtime is still not available. Restart Windows if requested, then run SayWhat? setup again.' then begin
    Result := CustomMessage('Sw142');
    exit;
  end;
  if English = 'The Microsoft prerequisite could not be downloaded/verified: ' then begin
    Result := CustomMessage('Sw143');
    Result := Result + ' ';
    exit;
  end;
  if English = 'The SayWhat? app files are ready. Open SayWhat and choose Set up SayWhat? to finish preparing translation. Windows may ask for permission.' then begin
    Result := CustomMessage('Sw144');
    exit;
  end;
  if English = 'Closing SayWhat? safely. Waiting for translation tools and models to stop…' then begin
    Result := CustomMessage('Sw145');
    exit;
  end;
  if English = 'SayWhat? is still open. Choose Quit SayWhat? in its tray menu, wait for it to close, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw146');
    exit;
  end;
  if English = 'The selected installation could not be identified safely. Close SayWhat? manually, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw147');
    exit;
  end;
  if English = 'The app could not be asked to close safely. Quit SayWhat? from its tray menu, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw148');
    exit;
  end;
  if English = 'This installation is open in another Windows account or session. Ask that user to quit SayWhat?, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw149');
    exit;
  end;
  if English = 'SayWhat? has not finished closing. Wait for translation tools to stop, or choose Retry quit in the app, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw150');
    exit;
  end;
  if English = 'The app did not finish a verified shutdown. Quit SayWhat? manually, then retry setup. No files were changed.' then begin
    Result := CustomMessage('Sw151');
    exit;
  end;
  if English = 'Shutdown check code: ' then begin
    Result := CustomMessage('Sw152');
    Result := Result + ' ';
    exit;
  end;
end;

function SayWhatUiLanguage(Param: String): String;
begin
  Result := ActiveLanguage;
  if Result = 'zh_CN' then Result := 'zh-CN';
  if (Result <> 'zh-CN') and (Result <> 'ja') and (Result <> 'ko') then Result := 'en';
end;

procedure InitializeSayWhatInstaller;
var BodyFont, HeadingFont: String;
begin
  // Explicit warm charcoal backing avoids Inno's default blue welcome panel.
  // Native dark styling retains keyboard/high-contrast behavior.
  BodyFont := 'Segoe UI';
  if ActiveLanguage = 'zh_CN' then BodyFont := 'Microsoft YaHei UI'
  else if ActiveLanguage = 'ja' then BodyFont := 'Yu Gothic UI'
  else if ActiveLanguage = 'ko' then BodyFont := 'Malgun Gothic';
  HeadingFont := BodyFont;
  if ActiveLanguage = 'en' then HeadingFont := 'Georgia';
  WizardForm.Font.Name := BodyFont;
  WizardForm.WelcomeLabel1.Font.Name := HeadingFont;
  WizardForm.WelcomeLabel1.Font.Size := 21;
  WizardForm.WelcomeLabel1.Caption := SwT(InstallAction) + ' SayWhat?';
  WizardForm.WelcomeLabel2.Font.Name := BodyFont;
  WizardForm.PageNameLabel.Font.Name := HeadingFont;
  WizardForm.PageNameLabel.Font.Size := 16;
  WizardForm.PageNameLabel.Height := ScaleY(25);
  WizardForm.PageDescriptionLabel.Top := WizardForm.PageNameLabel.Top + ScaleY(28);
  WizardForm.PageDescriptionLabel.Font.Name := BodyFont;
  WizardForm.FinishedHeadingLabel.Font.Name := HeadingFont;
  WizardForm.FinishedHeadingLabel.Font.Size := 21;
  WizardForm.FinishedLabel.Font.Name := BodyFont;
  if not HighContrastActive then begin
    WizardForm.WelcomeLabel1.StyleElements := WizardForm.WelcomeLabel1.StyleElements - [seFont];
    WizardForm.WelcomeLabel1.Font.Color := StrToColor('#F6EFE7');
    WizardForm.WelcomeLabel2.StyleElements := WizardForm.WelcomeLabel2.StyleElements - [seFont];
    WizardForm.WelcomeLabel2.Font.Color := StrToColor('#F6EFE7');
    WizardForm.PageNameLabel.StyleElements := WizardForm.PageNameLabel.StyleElements - [seFont];
    WizardForm.PageNameLabel.Font.Color := StrToColor('#F28C78');
    WizardForm.PageDescriptionLabel.StyleElements := WizardForm.PageDescriptionLabel.StyleElements - [seFont];
    WizardForm.PageDescriptionLabel.Font.Color := StrToColor('#F6EFE7');
    WizardForm.FinishedHeadingLabel.StyleElements := WizardForm.FinishedHeadingLabel.StyleElements - [seFont];
    WizardForm.FinishedHeadingLabel.Font.Color := StrToColor('#F28C78');
  end;
  // Keep the square mark square instead of stretching it into a portrait banner.
  WizardForm.WizardBitmapImage.Height := WizardForm.WizardBitmapImage.Width;
  WizardForm.WizardBitmapImage.Top :=
    (WizardForm.WizardBitmapImage.Parent.ClientHeight - WizardForm.WizardBitmapImage.Height) div 2;
  WizardForm.WizardBitmapImage2.Height := WizardForm.WizardBitmapImage2.Width;
  WizardForm.WizardBitmapImage2.Top :=
    (WizardForm.WizardBitmapImage2.Parent.ClientHeight - WizardForm.WizardBitmapImage2.Height) div 2;
end;
