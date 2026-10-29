using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Unshown controls and a redirected DesktopTests data root only. No profile action
// that can save settings, start a speech engine or download a model is invoked.
internal static class TranslationProfilesUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Profile UI: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Get<T>(string name) => (T)(typeof(MainWindow).GetField(name, flags)?.GetValue(window)
            ?? throw new InvalidOperationException("Missing profile test field: " + name));
        var settings = Get<AppSettings>("_settings");
        var runtime = Get<RuntimeController>("_runtime");
        Check(Get<bool>("_preview") && !window.IsVisible && window.Dispatcher.CheckAccess(), "requires an unshown preview on the UI thread");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "test begins with all engines stopped");
        string? fixture = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(fixture) && string.Equals(Path.GetFullPath(fixture), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase), "requires the desktop runner's redirected fixture data root");
        AppSettings before = settings.Copy();
        string pageBefore = Get<string>("_page"), languageBefore = UiText.Language;
        var protectedPaths = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "saywhat.log") };
        var filesBefore = protectedPaths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.OrdinalIgnoreCase);
        try
        {
            TranslationProfiles.CopyInto(TranslationProfiles.Select(settings, TranslationProfiles.NormalId), settings);
            window.Navigate("Settings");
            var section = Nodes(window).OfType<StackPanel>().Single(node => node.Name == "TranslationProfilesSection");
            var card = section.Parent as Border;
            Check(card?.Parent is StackPanel page && page.Children.IndexOf(card) <= 1, "profile controls appear at the top of Settings");
            ComboBox picker = Get<ComboBox>("_translationProfilesPicker");
            Check(picker.Name == "TranslationProfilePicker" && picker.ItemTemplate is not null, "dedicated profile picker uses a localized display template");
            Check(picker.Items.OfType<TranslationProfileUiChoice>().Where(choice => choice.IsBuiltIn).Select(choice => choice.Id)
                .SequenceEqual(new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId, TranslationProfiles.GroupsId }), "all three built-ins are selectable in Settings");
            Check((picker.SelectedItem as TranslationProfileUiChoice)?.Id == TranslationProfiles.NormalId, "current profile is initially selected");
            string draftBeforeSelection = JsonSerializer.Serialize(settings);
            picker.SelectedItem = picker.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == TranslationProfiles.GroupsId);
            Check(JsonSerializer.Serialize(settings) == draftBeforeSelection, "selecting Groups previews it without applying settings");
            Check(Get<Button>("_translationProfileApplyButton").IsEnabled && Get<TextBlock>("_translationProfileExperimental").Visibility == Visibility.Visible,
                "unapplied Groups offers a clear Apply action with one experimental marker");
            Check(!Get<Button>("_translationProfileRenameButton").IsEnabled && !Get<Button>("_translationProfileDeleteButton").IsEnabled && !Get<Button>("_translationProfileUpdateButton").IsEnabled,
                "built-in profiles cannot be renamed, deleted or overwritten");
            picker.SelectedItem = picker.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == TranslationProfiles.QuickId);
            Check(Get<TextBlock>("_translationProfileExperimental").Visibility == Visibility.Collapsed && JsonSerializer.Serialize(settings) == draftBeforeSelection,
                "Quick is available here without changing the current engine during selection");

            var quick = TranslationProfiles.Select(settings, TranslationProfiles.QuickId);
            TranslationProfiles.CopyInto(quick, settings);
            string quickBeforeAdvanced = JsonSerializer.Serialize(settings);
            window.Navigate("Advanced");
            Check(JsonSerializer.Serialize(settings) == quickBeforeAdvanced && settings.IncomingSettleMilliseconds == 800
                && settings.SubtitlePreviewStableMilliseconds == 250 && settings.RecognitionDelayMilliseconds == 160,
                "opening Advanced preserves Quick timings instead of clamping them to slower slider minimums");
            TranslationProfiles.CopyInto(TranslationProfiles.Select(settings, TranslationProfiles.NormalId), settings);

            var custom = TranslationProfiles.Create(settings, "Settings");
            TranslationProfiles.CopyInto(custom, settings);
            window.Navigate("Settings"); picker = Get<ComboBox>("_translationProfilesPicker");
            Check((picker.SelectedItem as TranslationProfileUiChoice)?.Name == "Settings" && !TranslationProfiles.IsModified(settings), "a custom snapshot is selected and marked unmodified");
            Check(Get<Button>("_translationProfileRenameButton").IsEnabled && Get<Button>("_translationProfileDeleteButton").IsEnabled && !Get<Button>("_translationProfileUpdateButton").IsEnabled,
                "saved custom profiles support rename/delete while unchanged snapshots cannot be overwritten needlessly");
            settings.OwnVoiceTargetLanguage = settings.OwnVoiceTargetLanguage == "Chinese" ? "Japanese" : "Chinese";
            window.RefreshTranslationProfilesUi();
            Check(Get<Button>("_translationProfileUpdateButton").IsEnabled && Get<Button>("_translationProfileApplyButton").IsEnabled, "editing a saved custom snapshot exposes update and apply actions");
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                window.Navigate("Settings"); picker = Get<ComboBox>("_translationProfilesPicker");
                var choices = picker.Items.OfType<TranslationProfileUiChoice>().ToArray();
                Check(choices.Single(choice => choice.Id == TranslationProfiles.QuickId).ToString() == UiText.T("Quick")
                    && choices.Single(choice => choice.Id == TranslationProfiles.NormalId).ToString() == UiText.T("Normal")
                    && choices.Single(choice => choice.Id == TranslationProfiles.GroupsId).ToString() == UiText.T("Groups"), "built-in labels are localized in " + language);
                Check(choices.Single(choice => choice.Id == custom.ActiveTranslationProfileId).ToString() == "Settings", "saved profile names remain literal user text in " + language);
                Check(Get<Button>("_translationProfileApplyButton").Content?.ToString() == UiText.T("Apply profile")
                    && BindingOperations.IsDataBound(Get<TextBlock>("_translationProfileState"), TextBlock.TextProperty), "actions and changing status stay bound to the interface language");
                var dialog = window.CreateTranslationProfileNameDialog("Save profile", "Name this set of settings.", "Literal user name", null);
                Check(dialog.Background == ModernTheme.Background && dialog.Foreground == ModernTheme.Text && !dialog.Topmost && dialog.Title == UiText.T("Save profile"), "profile dialog follows the active theme and language without becoming an overlay");
                TextBox input = Nodes(dialog).OfType<TextBox>().Single();
                Check(input.MaxLength == 60 && input.Text == "Literal user name" && AutomationProperties.GetName(input) == UiText.T("Profile name"), "name field is bounded, accessible and never translates editable text");
                dialog.Close();
            }
            Check(!runtime.OwnRunning && !runtime.IncomingRunning, "profile previews did not start models or capture audio");
            Check(filesBefore.All(pair => pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "read-only UI checks did not mutate fixture settings, configs or logs");
            return checks;
        }
        finally
        {
            TranslationProfiles.CopyInto(before, settings);
            UiText.Language = languageBefore;
            window.Navigate(pageBefore);
        }
    }

    // Dedicated visual fixtures, never a displayed window. Rendering must not
    // raise Loaded, run the apply callback, or touch the user's settings.
    internal static int RenderPreview(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Profile preview: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)
            ?? throw new InvalidOperationException("Missing profile preview field: " + name);
        T Get<T>(string name) => (T)Field(name).GetValue(window)!;
        var runtime = Get<RuntimeController>("_runtime");
        Check(Get<bool>("_preview") && !window.IsVisible && !window.IsLoaded && window.Dispatcher.CheckAccess()
            && !runtime.OwnRunning && !runtime.IncomingRunning, "requires an unloaded inert dashboard with stopped tools");
        string? fixture = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        directory = Path.GetFullPath(directory);
        Check(!string.IsNullOrWhiteSpace(fixture) && string.Equals(Path.GetFullPath(fixture), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Directory.GetParent(Path.GetFullPath(AppPaths.Root))?.FullName, directory, StringComparison.OrdinalIgnoreCase),
            "output belongs to the isolated test data fixture");
        AppSettings settings = Get<AppSettings>("_settings"), before = settings.Copy();
        string settingsJsonBefore = JsonSerializer.Serialize(settings), pageBefore = Get<string>("_page"), languageBefore = UiText.Language;
        object? noticeBefore = Field("_translationProfileNotice").GetValue(window);
        var content = (FrameworkElement)window.Content;
        Size priorSize = content.RenderSize;
        ScrollViewer mainScroll = Nodes(content).OfType<ScrollViewer>().Single(scroll => ReferenceEquals(scroll.Content, Get<ContentControl>("_content")));
        double offsetBefore = mainScroll.VerticalOffset;
        var protectedFiles = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "saywhat.log") }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var metrics = new List<string>();
        SubtitleWindow? overlay = null;
        void DrainPresentation()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        void Layout(FrameworkElement element, Size size)
        {
            foreach (UIElement node in LayoutNodes(element).OfType<UIElement>())
            { node.InvalidateMeasure(); node.InvalidateArrange(); }
            element.Measure(size); element.Arrange(new Rect(new Point(), size)); element.UpdateLayout();
        }
        void Render(FrameworkElement element, Size size, string filename)
        {
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, filename)); encoder.Save(file);
            checks++;
        }
        try
        {
            var preview = TranslationProfiles.Select(before, TranslationProfiles.NormalId);
            preview.FirstRunSetupCompleted = true;
            preview.UiLanguage = "en"; preview.UiThemeId = "coral-night";
            preview.CustomThemeImagePath = "";
            string name = "Friends chat";
            while (preview.CustomTranslationProfiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name += " example";
            preview = TranslationProfiles.Create(preview, name);
            preview = TranslationProfiles.Select(preview, TranslationProfiles.NormalId);
            TranslationProfiles.CopyInto(preview, settings);
            Field("_translationProfileNotice").SetValue(window, null);
            UiText.Language = "en"; AppearanceTheme.Apply(preview); ThemeBackground.Apply(preview);
            window.Navigate("Settings");
            ComboBox picker = Get<ComboBox>("_translationProfilesPicker");
            picker.SelectedItem = picker.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == TranslationProfiles.NormalId);
            foreach (Size size in new[] { new Size(1160, 800), new Size(900, 650) })
            {
                Layout(content, size); MainWindow.ApplySlimScrollbars(content); DrainPresentation();
                mainScroll.ScrollToTop(); DrainPresentation(); Layout(content, size); DrainPresentation(); Layout(content, size);
                // Theme/localization work can rebuild the page during the drain.
                // Measure its active selector rather than a detached old control.
                picker = Get<ComboBox>("_translationProfilesPicker");
                Rect pickerBounds = picker.TransformToAncestor(mainScroll).TransformBounds(new Rect(new Point(), picker.RenderSize));
                Check(mainScroll.VerticalOffset < .5 && mainScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                    && mainScroll.ScrollableWidth < 1, "Settings starts at the profile card without horizontal overflow at " + size);
                Check(picker.ActualWidth > 250 && pickerBounds.Left >= -.5 && pickerBounds.Top >= -.5
                    && pickerBounds.Right <= mainScroll.ActualWidth + .5 && pickerBounds.Bottom <= mainScroll.ActualHeight + .5,
                    "profile selector remains fully visible at " + size);
                metrics.Add(FormattableString.Invariant($"Settings {size.Width:0}x{size.Height:0}: offset={mainScroll.VerticalOffset:0.0}; horizontalOverflow={mainScroll.ScrollableWidth:0.0}; picker={pickerBounds.X:0.0},{pickerBounds.Y:0.0},{pickerBounds.Width:0.0},{pickerBounds.Height:0.0}"));
                Render(content, size, size.Width == 1160 ? "profiles-settings.png" : "profiles-settings-narrow.png");
            }
            picker.SelectedItem = picker.Items.OfType<TranslationProfileUiChoice>().Single(choice => choice.Id == TranslationProfiles.GroupsId);
            Layout(content, new Size(1160, 800)); mainScroll.ScrollToTop(); DrainPresentation(); Layout(content, new Size(1160, 800));
            Render(content, new Size(1160, 800), "profiles-settings-groups.png");
            Check(settings.ActiveTranslationProfileId == TranslationProfiles.NormalId && settings.IncomingSpeechEngine == "voxtral", "Groups visual selection remains unapplied");

            overlay = new SubtitleWindow();
            typeof(SubtitleWindow).GetField("_settings", flags)!.SetValue(overlay, preview.Copy());
            int callbackCalls = 0;
            overlay.ProfileChangeRequested = _ => { callbackCalls++; return Task.CompletedTask; };
            overlay.RefreshInterfaceLanguage();
            var status = (TextBlock)typeof(SubtitleWindow).GetField("_status", flags)!.GetValue(overlay)!;
            status.Text = UiText.T("Listening to VRChat");
            var source = (TextBlock)typeof(SubtitleWindow).GetField("_source", flags)!.GetValue(overlay)!;
            source.Text = "Jun: 我们在桥边见吧。";
            overlay.Publish(new("Mira", "你好，很高兴认识你。", "Hey! It's nice to meet you.", 301, DateTimeOffset.UtcNow.AddSeconds(-8)));
            overlay.Publish(new("Jun", "我们在桥边见吧。", "Let's meet by the bridge.", 302, DateTimeOffset.UtcNow.AddSeconds(-3)));
            var normal = (Button)typeof(SubtitleWindow).GetField("_normalProfileButton", flags)!.GetValue(overlay)!;
            var groups = (Button)typeof(SubtitleWindow).GetField("_groupsProfileButton", flags)!.GetValue(overlay)!;
            Check(normal.IsEnabled && groups.IsEnabled && ReferenceEquals(normal.Foreground, AppearanceTheme.Good), "overlay previews enabled mode controls with Normal selected");
            var overlayContent = overlay.TranscriptRoot;
            var overlaySize = new Size(960, 430);
            Layout(overlayContent, overlaySize); DrainPresentation(); Layout(overlayContent, overlaySize);
            Render(overlayContent, overlaySize, "subtitle-profiles.png");
            Check(LogicalTreeHelper.GetParent(overlayContent) is null && Nodes(overlayContent).Contains(normal) &&
                !Nodes((FrameworkElement)overlay.Content).Contains(normal),
                "the profile preview renders detached transcript controls without expanding the compact toolbar");
            Check(callbackCalls == 0 && !overlay.IsLoaded && !overlay.IsVisible && overlay.SpeakerFeedbackHost is null,
                "overlay rendering never starts a service or invokes a profile switch");
            File.WriteAllLines(Path.Combine(directory, "profile-preview-layout.txt"), metrics);
        }
        finally
        {
            if (overlay is not null) { overlay.ProfileChangeRequested = null; overlay.Close(); }
            TranslationProfiles.CopyInto(before, settings);
            Field("_translationProfileNotice").SetValue(window, noticeBefore);
            AppearanceTheme.Apply(before); ThemeBackground.Apply(before); UiText.Language = languageBefore;
            window.Navigate(pageBefore);
            if (priorSize.Width > 0 && priorSize.Height > 0) Layout(content, priorSize);
            mainScroll.ScrollToVerticalOffset(offsetBefore); DrainPresentation();
        }
        Check(JsonSerializer.Serialize(settings) == settingsJsonBefore && Get<string>("_page") == pageBefore && UiText.Language == languageBefore,
            "preview restores settings, page and interface language");
        Check(protectedFiles.All(pair => pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
            "preview never saves preferences, runtime configs or translation logs");
        Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning, "all tools remained stopped");
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }

    private static IEnumerable<DependencyObject> LayoutNodes(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out DependencyObject? node))
        {
            if (!seen.Add(node)) continue;
            yield return node;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (node is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }
    }
}
