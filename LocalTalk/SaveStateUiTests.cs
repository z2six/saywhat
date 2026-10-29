using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace IncomingTranslator;

internal static class SaveStateUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = RunPureChecks();
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Save state UI: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)
            ?? throw new InvalidOperationException("Missing save-state test field: " + name);
        T Get<T>(string name) => (T)Field(name).GetValue(window)!;
        void Set(string name, object? value) => Field(name).SetValue(window, value);
        var settings = Get<AppSettings>("_settings");
        var runtime = Get<RuntimeController>("_runtime");
        Check(Get<bool>("_preview") && !window.IsVisible && !window.IsLoaded && window.Dispatcher.CheckAccess()
            && !runtime.OwnRunning && !runtime.IncomingRunning, "requires an inert unloaded preview with both engines stopped");
        string? fixture = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(fixture)
            && string.Equals(Path.GetFullPath(fixture), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFullPath(AppPaths.Root), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SayWhat"), StringComparison.OrdinalIgnoreCase),
            "writes are restricted to the redirected test data fixture");
        window.InitializeSaveStateTracking();
        AppSettings before = settings.Copy();
        AppSettings? snapshotBefore = Get<AppSettings?>("_saveStateAppliedSettings")?.Copy();
        string pageBefore = Get<string>("_page"), languageBefore = UiText.Language;
        bool busyBefore = Get<bool>("_busy"), closingBefore = Get<bool>("_closing");
        SetupSelection setupBefore = Get<SetupSelection>("_savedSetupSelection");
        var protectedPaths = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "saywhat.log") };
        var filesBefore = protectedPaths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        void Drain()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Button Save() => Get<Button>("_saveButton");
        void CleanFromSaved()
        {
            AppSettings saved = SettingsStore.Load(); saved.FirstRunSetupCompleted = true;
            TranslationProfiles.CopyInto(saved, settings);
            Set("_saveStateAppliedSettings", saved.Copy());
            Set("_busy", false); Set("_closing", false);
        }
        void RefreshSetup(bool download) => typeof(MainWindow).GetMethod("RefreshSetupAction", flags)!.Invoke(window, [download]);
        try
        {
            UiText.Language = "en";
            CleanFromSaved();
            foreach (string page in new[] { "Languages", "Settings", "Translation", "Audio & timing" })
            {
                string unchanged = JsonSerializer.Serialize(settings);
                window.Navigate(page); Drain(); window.RefreshSaveState();
                Check(JsonSerializer.Serialize(settings) == unchanged && !window.HasPendingSaveChanges && !Save().IsEnabled,
                    "building/navigating " + page + " does not create dirty settings");
                Check(ReferenceEquals(Save().Background, ModernTheme.Raised) && ReferenceEquals(Save().Foreground, ModernTheme.Muted),
                    "clean save has the muted theme roles on " + page);
            }
            window.Navigate("Languages"); Drain();
            var target = Nodes(window).OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == UiText.T("Translate my voice to"));
            string oldTarget = settings.OwnVoiceTargetLanguage;
            target.SelectedItem = oldTarget == "Japanese" ? "Chinese" : "Japanese";
            Drain();
            Check(window.HasPendingSaveChanges && Save().IsEnabled && ReferenceEquals(Save().Background, ModernTheme.Accent), "a target-language change enables the primary save");
            target.SelectedItem = oldTarget; Drain();
            Check(!window.HasPendingSaveChanges && !Save().IsEnabled, "reverting a language returns to clean without a save");

            var glossary = Nodes(window).OfType<TextBox>().Single(box => box.AcceptsReturn);
            string oldGlossary = glossary.Text;
            object? cachedSnapshot = Field("_saveStateAppliedSettings").GetValue(window);
            for (int index = 0; index < 20; index++) glossary.Text += "x";
            Check(Get<bool>("_saveStateRefreshQueued"), "many text changes queue one deferred refresh");
            Drain();
            Check(Save().IsEnabled && !Get<bool>("_saveStateRefreshQueued") && ReferenceEquals(cachedSnapshot, Field("_saveStateAppliedSettings").GetValue(window)),
                "typing uses the cached saved snapshot rather than re-reading settings");
            glossary.Text = oldGlossary; Drain();
            Check(!Save().IsEnabled, "reverting text clears dirty state");

            window.Navigate("Audio & timing"); Drain();
            Slider timing = Nodes(window).OfType<Slider>().First();
            double oldTiming = timing.Value;
            timing.Value = oldTiming + timing.TickFrequency; Drain();
            Check(Save().IsEnabled, "a timing slider change enables save");
            timing.Value = oldTiming; Drain();
            Check(!Save().IsEnabled, "reverting a timing slider disables save");
            CheckBox messages = Nodes(window).OfType<CheckBox>().Single(check => check.Content?.ToString() == UiText.T("Show translations while I speak"));
            bool? oldMessages = messages.IsChecked;
            messages.IsChecked = oldMessages != true; Drain();
            Check(Save().IsEnabled, "a checkbox change enables save");
            messages.IsChecked = oldMessages; Drain();
            Check(!Save().IsEnabled, "reverting a checkbox disables save");

            window.Navigate("Settings"); Drain();
            var profiles = Get<ComboBox>("_translationProfilesPicker");
            string currentProfile = settings.ActiveTranslationProfileId;
            profiles.SelectedItem = profiles.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == (currentProfile == TranslationProfiles.GroupsId ? TranslationProfiles.NormalId : TranslationProfiles.GroupsId));
            Drain();
            Check(window.HasPendingSaveChanges && Save().IsEnabled && settings.ActiveTranslationProfileId == currentProfile,
                "a selected but unapplied profile is a pending change without loading models");
            profiles.SelectedItem = profiles.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == currentProfile); Drain();
            Check(!Save().IsEnabled, "reselecting the active profile clears the pending profile choice");

            window.Navigate("Languages"); Drain();
            settings.OwnVoiceTargetLanguage = oldTarget == "Japanese" ? "Chinese" : "Japanese";
            window.RefreshSaveState();
            Set("_busy", true); window.RefreshSaveState();
            Check(!Save().IsEnabled && window.HasPendingSaveChanges, "active work disables save without erasing a draft");
            Set("_busy", false); Set("_closing", true); window.RefreshSaveState();
            Check(!Save().IsEnabled && window.HasPendingSaveChanges, "closing disables save without erasing a draft");
            Set("_closing", false); window.RefreshSaveState();
            Check(Save().IsEnabled, "an unapplied draft remains actionable after a task/cancel");

            settings.UiThemeId = settings.UiThemeId == "coral-night" ? "paper" : "coral-night";
            // Targeted saves into the isolated fixture only; no UI or engine is launched.
            SettingsStore.SaveAppearancePreference(settings);
            window.RefreshSaveState(reloadSaved: true);
            Check(Save().IsEnabled && settings.OwnVoiceTargetLanguage != oldTarget,
                "an immediate theme save does not swallow an unrelated language draft");
            settings.OwnVoiceTargetLanguage = oldTarget; window.RefreshSaveState();
            Check(!Save().IsEnabled, "an already persisted theme alone does not need Save & apply");

            SettingsStore.SaveUiLanguagePreference("ko");
            window.ChangeInterfaceLanguage("ko"); window.RefreshSaveState(reloadSaved: true); Drain();
            Check(!Save().IsEnabled && !window.HasPendingSaveChanges && settings.UiLanguage == "ko", "an immediate interface-language save stays clean");
            UiText.Language = "en";
            window.Navigate("Languages");
            settings.IncomingTargetLanguage = settings.IncomingTargetLanguage == "English" ? "Chinese" : "English";
            window.RefreshSaveState(); Check(Save().IsEnabled, "incoming translation-language changes need a save");
            string pendingBeforeLockedRead = JsonSerializer.Serialize(settings);
            object? snapshotBeforeLockedRead = Field("_saveStateAppliedSettings").GetValue(window);
            using (var locked = File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                window.RefreshSaveState(reloadSaved: true);
            Check(JsonSerializer.Serialize(settings) == pendingBeforeLockedRead && ReferenceEquals(snapshotBeforeLockedRead, Field("_saveStateAppliedSettings").GetValue(window))
                && Save().IsEnabled, "an unreadable saved file does not replace the draft or cached state with defaults");
            SettingsStore.SaveTranslationProfileSelection(settings);
            window.RefreshSaveState(reloadSaved: true);
            Check(!Save().IsEnabled && !window.HasPendingSaveChanges, "a successful save immediately disables Save & apply");
            string savedBytes = File.ReadAllText(AppPaths.SettingsPath);
            for (int index = 0; index < 10; index++) window.RefreshSaveState();
            Check(!Save().IsEnabled && File.ReadAllText(AppPaths.SettingsPath) == savedBytes, "repeated clean refreshes do not save or rewrite configuration");

            settings.FirstRunSetupCompleted = true;
            Set("_savedSetupSelection", SetupSelection.Capture(settings));
            window.Navigate("Setup"); RefreshSetup(false); window.RefreshSaveState();
            Check(!Save().IsEnabled, "unchanged prepared Setup has no actionable Save & apply");
            var alternateSetupBaseline = settings.Copy();
            alternateSetupBaseline.OwnManagedModelId = alternateSetupBaseline.OwnManagedModelId == "hy-mt2-1.8b-q4_k_m"
                ? "hy-mt2-7b-q4_k_m" : "hy-mt2-1.8b-q4_k_m";
            Set("_savedSetupSelection", SetupSelection.Capture(alternateSetupBaseline));
            RefreshSetup(false); window.RefreshSaveState();
            Check(!window.HasPendingSaveChanges && Get<Button>("_setupActionButton").IsEnabled && Save().IsEnabled,
                "the sticky Setup action follows its own saved-choice baseline rather than a second global veto");
            Set("_savedSetupSelection", SetupSelection.Capture(settings));
            RefreshSetup(false); window.RefreshSaveState();
            Check(!Save().IsEnabled, "a clean scoped Setup baseline immediately disables its sticky save again");
            RefreshSetup(true); window.RefreshSaveState();
            Check(Save().IsEnabled && Save().Content?.ToString() == UiText.T("Download & apply"), "missing setup files retain their explicit preparation action");
            Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning, "all dirty-state tests kept capture and models stopped");
            Check(filesBefore.Where(pair => pair.Key.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).All(pair =>
                pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "tests did not add runtime logs");
        }
        finally
        {
            foreach (var pair in filesBefore)
                if (pair.Value is not null) File.WriteAllBytes(pair.Key, pair.Value);
                else if (File.Exists(pair.Key)) File.Delete(pair.Key);
            TranslationProfiles.CopyInto(before, settings);
            Set("_saveStateAppliedSettings", snapshotBefore); Set("_savedSetupSelection", setupBefore);
            Set("_busy", busyBefore); Set("_closing", closingBefore);
            AppearanceTheme.Apply(before); ThemeBackground.Apply(before); UiText.Language = languageBefore;
            window.Navigate(pageBefore); Drain(); window.RefreshSaveState();
        }
        return checks;
    }

    private static int RunPureChecks()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Save state comparison: " + detail); checks++; }
        var saved = new AppSettings { UseManagedTranslator = true };
        Check(!SettingsSaveState.HasChanges(saved.Copy(), saved), "equal settings are clean");
        var derived = saved.Copy(); derived.OwnTranslatorBaseUrl = "http://127.0.0.1:1235/v1"; derived.OwnTranslatorModel = "local-runtime-alias";
        Check(!SettingsSaveState.HasChanges(derived, saved), "managed endpoint/alias normalization does not create dirty state");
        var draft = saved.Copy(); draft.OwnVoiceTargetLanguage = "Japanese"; draft.Glossary = "private draft";
        Check(SettingsSaveState.HasChanges(draft, saved), "real language/glossary settings are dirty");
        var latest = saved.Copy(); latest.UiThemeId = "paper"; latest.UiLanguage = "ko"; latest.SubtitlesAlwaysOnTop = false;
        string draftBefore = JsonSerializer.Serialize(draft), savedBefore = JsonSerializer.Serialize(saved), latestBefore = JsonSerializer.Serialize(latest);
        AppSettings rebased = SettingsSaveState.Rebase(draft, saved, latest);
        Check(rebased.OwnVoiceTargetLanguage == "Japanese" && rebased.Glossary == "private draft" && rebased.UiThemeId == "paper"
            && rebased.UiLanguage == "ko" && !rebased.SubtitlesAlwaysOnTop, "targeted persisted preferences merge without replacing unrelated drafts");
        Check(SettingsSaveState.HasChanges(rebased, latest), "targeted autosaves leave the remaining draft dirty");
        Check(!SettingsSaveState.HasChanges(SettingsSaveState.Rebase(saved.Copy(), saved, latest), latest), "targeted preferences alone remain clean");
        draft.UiThemeId = "sage";
        Check(SettingsSaveState.Rebase(draft, saved, latest).UiThemeId == "sage", "a pending custom-profile theme is not overwritten by a newer theme preference");
        Check(JsonSerializer.Serialize(saved) == savedBefore && JsonSerializer.Serialize(latest) == latestBefore,
            "rebasing never mutates the saved snapshots");
        draft.UiThemeId = saved.UiThemeId;
        Check(JsonSerializer.Serialize(draft) == draftBefore, "rebasing never mutates the caller's draft");
        Check(SettingsSaveState.HasChanges(saved.Copy(), saved, TranslationProfiles.GroupsId), "an unapplied profile selection is pending even before fields change");
        Check(!SettingsSaveState.HasChanges(saved.Copy(), saved, TranslationProfiles.NormalId), "the active profile selection is clean");
        var named = TranslationProfiles.Create(saved, "My profile");
        Check(!SettingsSaveState.HasChanges(named, saved, named.ActiveTranslationProfileId), "saved-profile metadata is not a runtime settings edit");
        named.OwnVoiceTargetLanguage = "Japanese";
        Check(SettingsSaveState.HasChanges(named, saved), "custom-profile field edits are real pending changes");
        latest.OwnVoiceAdditionalTargetLanguages = new() { "Korean" };
        var isolated = SettingsSaveState.Rebase(saved.Copy(), saved, latest);
        isolated.OwnVoiceAdditionalTargetLanguages.Add("English");
        Check(latest.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Korean" }), "rebased lists do not alias saved data");
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }
}
