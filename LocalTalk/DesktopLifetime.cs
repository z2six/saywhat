using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace IncomingTranslator;

internal enum DesktopCloseAction { Ask, Quit, Tray, Cancel }
internal sealed record DesktopCloseChoice(DesktopCloseAction Action, bool Remember);

// Owns dashboard lifetime only. Hiding this window must not close live subtitles, stop capture,
// unload a model, or dispose the runtime. Quit delegates cleanup to the exact-owned runtime API.
internal sealed class DesktopLifetime
{
    private readonly Window _window;
    private readonly RuntimeController _runtime;
    private readonly LocalTalkProvisioner _provisioner;
    private readonly AppSettings _settings;
    private readonly Action _cancelAndDetach;
    private readonly Action _markClosing;
    private readonly bool _preview;
    private readonly bool _createTrayIcon;
    private readonly bool _activateOnRestore;
    private readonly bool _enableInstallerShutdown;
    private readonly Func<Task>? _waitForOperations;
    private readonly Action? _cancelPendingOperation;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayOpen, _trayStop, _trayQuit;
    private bool _trayLanguageSubscribed;
    private bool _trayThemeSubscribed;
    private Application? _application;
    private InstallerShutdownServer? _installerShutdown;
    private Window? _closeChoiceWindow;
    private Task _quitTask = Task.CompletedTask;
    private bool _choosing, _quitScheduled, _hideScheduled, _quitInProgress, _quitFailed, _allowFinalClose, _closed;
    private bool _callbacksInvoked, _runtimeStopped, _provisionerDisposed, _sessionEnding;
    private bool _installerQuitRequested;

    internal DesktopLifetime(Window window, RuntimeController runtime, LocalTalkProvisioner provisioner,
        AppSettings settings, Action cancelAndDetach, Action markClosing, bool preview,
        Func<Task>? waitForOperations = null, Action? cancelPendingOperation = null, bool createTrayIcon = true,
        bool activateOnRestore = true, bool enableInstallerShutdown = true)
    {
        _window = window; _runtime = runtime; _provisioner = provisioner; _settings = settings;
        _cancelAndDetach = cancelAndDetach; _markClosing = markClosing;
        _preview = preview; _waitForOperations = waitForOperations;
        _createTrayIcon = createTrayIcon;
        _activateOnRestore = activateOnRestore;
        _enableInstallerShutdown = enableInstallerShutdown;
        _cancelPendingOperation = cancelPendingOperation;
        _window.Loaded += OnLoaded;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
    }

    public void RequestQuit()
    {
        Dispatch(() =>
        {
            if (_closed || _allowFinalClose || _quitScheduled || _quitInProgress) return;
            _quitScheduled = true;
            // Publish the queued operation immediately: SessionEnding must observe this work,
            // not the previous completed task, even before its dispatcher callback can run.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _quitTask = completion.Task;
            _ = _window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                _quitScheduled = false;
                try
                {
                    if (_closed || _allowFinalClose) return;
                    if (_preview) { _allowFinalClose = true; DeferClose(); return; }
                    await QuitAsync();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { completion.TrySetResult(); }
            }), DispatcherPriority.Background);
        });
    }

    internal void RequestInstallerQuit()
    {
        Dispatch(() =>
        {
            _installerQuitRequested = true;
            // An explicit installer request does not invoke or save Ask/Tray policy.
            // Dismiss an already-open close-choice dialog before queued cleanup runs.
            _closeChoiceWindow?.Close();
            RequestQuit();
        });
    }

    public void Restore()
    {
        // A restore request can arrive through a nested dialog/Closing message loop. Show and
        // Hide are illegal on the closing window until that stack has unwound.
        if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished) return;
        _ = _window.Dispatcher.BeginInvoke(new Action(RestoreCore), DispatcherPriority.Background);
    }

    private void RestoreCore()
    {
        if (_closed || _allowFinalClose) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        if (_activateOnRestore && !_window.IsActive) _window.Activate();
        // Do not touch a separate subtitle window's visibility or always-on-top setting.
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_preview || _closed) return;
        if (_enableInstallerShutdown && _installerShutdown is null)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                _installerShutdown = new InstallerShutdownServer(Environment.ProcessPath ?? throw new InvalidOperationException("The app path is unavailable."),
                    process.SessionId, RequestInstallerQuit);
            }
            catch (Exception error) { Log("Installer shutdown control is unavailable; manual Quit remains available: " + error.GetType().Name); }
        }
        if (!_createTrayIcon || _tray is not null) return;
        EnsureTray();
        _application = Application.Current;
        if (_application is not null) _application.SessionEnding += OnSessionEnding;
    }

    private bool EnsureTray()
    {
        if (_preview || !_createTrayIcon || _closed) return false;
        if (_tray is not null) return true;
        Forms.NotifyIcon? icon = null;
        Forms.ContextMenuStrip? menu = null;
        try
        {
            _trayIcon = BrandArtwork.CreateTrayIcon();
            menu = new Forms.ContextMenuStrip
            {
                BackColor = TrayColor(AppearanceTheme.Surface),
                ForeColor = TrayColor(AppearanceTheme.Text),
                Renderer = new ThemeTrayRenderer()
            };
            var open = new Forms.ToolStripMenuItem(UiText.T("Open SayWhat?"));
            open.Click += (_, _) => Restore();
            var stop = new Forms.ToolStripMenuItem(UiText.T("Stop translation"));
            stop.Click += (_, _) => Dispatch(() => { _ = StopTranslationsFromTrayAsync(stop); });
            var quit = new Forms.ToolStripMenuItem(UiText.T("Quit SayWhat?"));
            quit.Click += (_, _) => RequestQuit();
            menu.Items.Add(open); menu.Items.Add(stop); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(quit);
            icon = new Forms.NotifyIcon { Icon = _trayIcon, Text = TrayTooltip(), ContextMenuStrip = menu };
            icon.DoubleClick += (_, _) => Restore();
            icon.Visible = true;
            _tray = icon;
            _trayOpen = open; _trayStop = stop; _trayQuit = quit;
            UiText.LanguageChanged += RefreshTrayLanguage;
            _trayLanguageSubscribed = true;
            AppearanceTheme.Changed += RefreshTrayTheme;
            _trayThemeSubscribed = true;
            RefreshTrayTheme();
            return true;
        }
        catch (Exception error)
        {
            Log("System tray could not be created: " + error);
            icon?.Dispose(); menu?.Dispose(); _trayIcon?.Dispose(); _trayIcon = null;
            return false;
        }
    }

    private static string TrayTooltip()
    {
        string text = UiText.T("SayWhat? · Voice translation");
        return text.Length > 63 ? text[..63] : text;
    }

    private void RefreshTrayLanguage() => Dispatch(() =>
    {
        if (_closed || _tray is null) return;
        if (_trayOpen is not null) _trayOpen.Text = UiText.T("Open SayWhat?");
        if (_trayStop is not null) _trayStop.Text = UiText.T("Stop translation");
        if (_trayQuit is not null) _trayQuit.Text = UiText.T("Quit SayWhat?");
        _tray.Text = TrayTooltip();
    });

    private void RefreshTrayTheme() => Dispatch(() =>
    {
        if (_closed || _tray?.ContextMenuStrip is not Forms.ContextMenuStrip menu) return;
        menu.BackColor = TrayColor(AppearanceTheme.Surface);
        menu.ForeColor = TrayColor(AppearanceTheme.Text);
        foreach (Forms.ToolStripItem item in menu.Items)
        { item.BackColor = menu.BackColor; item.ForeColor = menu.ForeColor; }
        menu.Invalidate();
    });

    private static System.Drawing.Color TrayColor(System.Windows.Media.SolidColorBrush brush) =>
        System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);

    private sealed class ThemeTrayColors : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => TrayColor(AppearanceTheme.Surface);
        public override System.Drawing.Color MenuBorder => TrayColor(AppearanceTheme.Stroke);
        public override System.Drawing.Color MenuItemBorder => TrayColor(AppearanceTheme.Focus);
        public override System.Drawing.Color MenuItemSelected => TrayColor(AppearanceTheme.Selection);
        public override System.Drawing.Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientMiddle => MenuItemSelected;
        public override System.Drawing.Color MenuItemPressedGradientEnd => MenuItemSelected;
        public override System.Drawing.Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override System.Drawing.Color SeparatorDark => TrayColor(AppearanceTheme.Stroke);
        public override System.Drawing.Color SeparatorLight => ToolStripDropDownBackground;
    }

    private sealed class ThemeTrayRenderer : Forms.ToolStripProfessionalRenderer
    {
        public ThemeTrayRenderer() : base(new ThemeTrayColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs args)
        {
            args.TextColor = TrayColor(args.Item.Enabled ? AppearanceTheme.Text : AppearanceTheme.Muted);
            base.OnRenderItemText(args);
        }
    }

    private async Task StopTranslationsFromTrayAsync(Forms.ToolStripMenuItem stop)
    {
        if (_closed || _quitScheduled || _quitInProgress) return;
        stop.Enabled = false;
        try
        {
            _cancelPendingOperation?.Invoke(); // Cancel only the current task, not the app's lifetime.
            await _runtime.StopAllAsync();
            Log("Translation stopped from the system tray; SayWhat? stayed open.");
        }
        catch (Exception error)
        {
            Log("Tray stop translation did not finish: " + error);
            if (!_closed && !_sessionEnding)
                try
                {
                    Restore();
                    AppearanceTheme.ShowMessage(_window, UiText.T("SayWhat? couldn't finish stopping translation. Open Home and try Stop translation again. Details were saved in the app's log."), AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception displayError) { Log("Tray stop warning could not be displayed: " + displayError); }
        }
        finally { if (!_closed && _tray is not null) stop.Enabled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowFinalClose || _closed || _preview) return;
        args.Cancel = true;
        if (_quitScheduled || _quitInProgress || _choosing) return;
        if (_quitFailed || _sessionEnding) { RequestQuit(); return; }
        DesktopCloseAction action = ResolveCloseAction(_settings.CloseButtonBehavior, false, false, false, _tray is not null);
        if (action == DesktopCloseAction.Ask)
        {
            _choosing = true;
            try
            {
                Window choiceWindow = CreateCloseChoiceDialog(_window, _tray is not null);
                _closeChoiceWindow = choiceWindow;
                if (choiceWindow.ShowDialog() != true || choiceWindow.Tag is not DesktopCloseChoice choice) return;
                action = choice.Action;
                if (ShouldRemember(choice, _preview)) RememberChoice(choice);
            }
            catch (Exception error) { Log("Close choice failed: " + error); return; }
            finally { _closeChoiceWindow = null; _choosing = false; }
        }
        if (action == DesktopCloseAction.Tray)
        {
            if (!EnsureTray())
            {
                AppearanceTheme.ShowMessage(_window, "SayWhat? couldn't create its tray icon. The window will stay open so you can still quit safely.", AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DeferHideToTray();
        }
        else if (action == DesktopCloseAction.Quit) RequestQuit();
    }

    private void RememberChoice(DesktopCloseChoice choice)
    {
        try
        {
            // Read the LAST SAVED settings, not dashboard fields containing unsaved language/model
            // edits. Remembering a close preference must never inadvertently save those changes.
            AppSettings next = WithClosePreference(SettingsStore.Load(), choice.Action);
            SettingsStore.Save(next);
            _settings.CloseButtonBehavior = next.CloseButtonBehavior;
        }
        catch (Exception error)
        {
            Log("Close preference could not be saved: " + error);
            AppearanceTheme.ShowMessage(_window, "This choice couldn't be remembered. It will still apply this time.", AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task QuitAsync()
    {
        if (_quitInProgress || _closed) return;
        _quitInProgress = true; _quitFailed = false;
        try
        {
            if (!_sessionEnding)
            {
                if (!_installerQuitRequested) RestoreCore();
                _window.Content = ShutdownView(false);
                _window.IsEnabled = false;
            }
            if (!_callbacksInvoked)
            {
                _callbacksInvoked = true;
                try { _markClosing(); } catch (Exception error) { Log("Mark dashboard closing: " + error); }
                try { _cancelAndDetach(); } catch (Exception error) { Log("Cancel dashboard operations: " + error); }
            }
            // StopAll also makes retries useful if an earlier DisposeAsync entered its terminal
            // state before one owned shutdown operation failed. It never targets another app.
            // Start it immediately: a stalled installer must not keep microphone capture running.
            Task stoppingRuntime = _runtime.StopAllAsync();
            Task finishingOperations = WaitForOperationsAsync();
            await Task.WhenAll(stoppingRuntime, finishingOperations);
            // A canceled setup task must finish unwinding before its provisioner is disposed.
            // Both operations are observed; a timeout/failure retains the app for an honest retry.
            await _runtime.DisposeAsync();
            if (_runtime.OwnRunning || _runtime.IncomingRunning)
                throw new InvalidOperationException("The translation runtime still reports an active feature after cleanup.");
            _runtimeStopped = true;
            DisposeProvisioner();
            _allowFinalClose = true;
            Log("SayWhat? quit cleanup completed; app-owned translation tools were released.");
            DeferClose(); // Close may otherwise recurse into the currently executing Closing event.
        }
        catch (Exception error)
        {
            _quitInProgress = false; _quitFailed = true;
            Log("SayWhat? did not finish quit cleanup; application retained for retry: " + error);
            if (!_sessionEnding)
            {
                try
                {
                    _window.IsEnabled = true;
                    _window.Content = ShutdownView(true);
                    RestoreCore();
                }
                catch (Exception displayError) { Log("Quit cleanup warning could not be displayed: " + displayError); }
            }
        }
    }

    private async Task WaitForOperationsAsync()
    {
        if (_waitForOperations is null) return;
        try { await _waitForOperations().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (OperationCanceledException) { Log("Canceled background operation finished during quit."); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("A canceled setup task has not finished. Its provisioner was retained for a safe quit retry.", error);
        }
    }

    private UIElement ShutdownView(bool failed)
    {
        var panel = new StackPanel { Background = AppearanceTheme.Surface, MaxWidth = 580, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(36) };
        panel.Children.Add(ModernTheme.Label(failed ? "SayWhat? hasn't quit yet" : "Stopping translation…", 28));
        panel.Children.Add(ModernTheme.Label(failed
            ? "Something couldn't finish stopping. Retry to release translation tools safely. Details were saved in the app's log."
            : "Unloading models and stopping translation tools. This may take a few seconds.", 15, ModernTheme.Muted));
        if (failed) panel.Children.Add(ModernTheme.Button("Retry quit", (_, _) => RequestQuit(), true));
        else panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 12, 0, 0), Foreground = ModernTheme.Accent });
        var view = new Grid { Background = ModernTheme.Background };
        view.Children.Add(ThemeBackground.CreateLayer());
        view.Children.Add(panel);
        UiLocalization.ApplyTree(view);
        return view;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args)
    {
        if (_closed || _preview) return;
        _sessionEnding = true;
        args.Cancel = false; // Never veto or hold Windows sign-out/shutdown indefinitely.
        RequestQuit();
        _ = ObserveSessionCleanupAsync();
    }

    private async Task ObserveSessionCleanupAsync()
    {
        // Windows may terminate the process before asynchronous WSL cleanup finishes. Windows job
        // handles still release owned Windows children; Linux owner-token shutdown is best effort.
        try { await _quitTask.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (Exception error) { Log("Windows session ending: cleanup did not acknowledge within the available interval (" + error.GetType().Name + "). No foreign processes were targeted."); }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _window.Loaded -= OnLoaded; _window.Closing -= OnClosing; _window.Closed -= OnClosed;
        if (_application is not null) _application.SessionEnding -= OnSessionEnding;
        _installerShutdown?.Dispose(); _installerShutdown = null;
        DisposeTray();
        if (_runtimeStopped || _preview)
            try { DisposeProvisioner(); } catch (Exception error) { Log("Provisioner cleanup on final close: " + error); }
    }

    private void DisposeProvisioner()
    {
        if (_provisionerDisposed) return;
        _provisioner.Dispose();
        _provisionerDisposed = true;
    }

    private void DisposeTray()
    {
        if (_trayLanguageSubscribed)
        {
            UiText.LanguageChanged -= RefreshTrayLanguage;
            _trayLanguageSubscribed = false;
        }
        if (_trayThemeSubscribed)
        {
            AppearanceTheme.Changed -= RefreshTrayTheme;
            _trayThemeSubscribed = false;
        }
        if (_tray is not null)
        {
            try { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
            catch (Exception error) { Log("Tray icon cleanup: " + error); }
            _tray = null;
        }
        _trayIcon?.Dispose(); _trayIcon = null;
        _trayOpen = null; _trayStop = null; _trayQuit = null;
    }

    private void DeferClose() => _ = _window.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_closed) _window.Close();
    }), DispatcherPriority.Background);

    // The caller has already established that the tray exists. This UI-only seam also lets
    // offline dispatcher checks cover Closing -> Hide without creating a real tray icon.
    internal void DeferHideToTray()
    {
        if (_closed || _hideScheduled || _quitScheduled || _quitInProgress || _allowFinalClose ||
            _window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished) return;
        _hideScheduled = true;
        _ = _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            _hideScheduled = false;
            if (_closed || _quitScheduled || _quitInProgress || _allowFinalClose) return;
            if (_window.IsVisible) _window.Hide();
            Log("Dashboard hidden in the system tray; translations and models were left running.");
        }), DispatcherPriority.Background);
    }

    internal Task QuitCompletion => _quitTask;

    private void Dispatch(Action action)
    {
        if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished) return;
        if (_window.Dispatcher.CheckAccess()) action();
        else _ = _window.Dispatcher.BeginInvoke(action);
    }

    private static void Log(string text) { try { DesktopLog.Write(text); } catch { } }

    // Policy and dialog seams are inert: neither creates a tray icon, shows a window, or starts an
    // engine. The dashboard's offline layout checks can render the returned dialog on their STA.
    internal static DesktopCloseAction ResolveCloseAction(string? preference, bool explicitQuit, bool sessionEnding, bool preview, bool trayAvailable)
    {
        if (preview || explicitQuit || sessionEnding) return DesktopCloseAction.Quit;
        return preference switch
        {
            "Quit" => DesktopCloseAction.Quit,
            "Tray" when trayAvailable => DesktopCloseAction.Tray,
            _ => DesktopCloseAction.Ask
        };
    }

    internal static bool ShouldRemember(DesktopCloseChoice choice, bool preview) => !preview && choice.Remember && (choice.Action is DesktopCloseAction.Quit or DesktopCloseAction.Tray);

    internal static AppSettings WithClosePreference(AppSettings latestSaved, DesktopCloseAction action)
    {
        if (action is not DesktopCloseAction.Quit and not DesktopCloseAction.Tray) throw new ArgumentException("Only Quit or Tray can be remembered.", nameof(action));
        AppSettings next = latestSaved.Copy();
        next.CloseButtonBehavior = action.ToString();
        return next;
    }

    internal static Window CreateCloseChoiceDialog(Window? owner = null, bool trayAvailable = true)
    {
        var dialog = new Window
        {
            Title = "Close SayWhat?", Owner = owner, Icon = BrandArtwork.WindowIcon, Width = 560, MinWidth = 480,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = ModernTheme.Background, Foreground = ModernTheme.Text, ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), UseLayoutRounding = true
        };
        var body = new StackPanel { Background = AppearanceTheme.Surface, Margin = new Thickness(28) };
        body.Children.Add(ModernTheme.Heading("Quit or keep running?", 28));
        body.Children.Add(ModernTheme.Label("Quit stops translations and unloads models. Keep running hides this window, but translations and subtitles stay active.", 14, ModernTheme.Muted));
        var remember = new CheckBox { Content = "Remember my choice", IsChecked = true, Margin = new Thickness(0, 10, 0, 14) };
        body.Children.Add(remember);
        void Choose(DesktopCloseAction action) { dialog.Tag = new DesktopCloseChoice(action, remember.IsChecked == true); dialog.DialogResult = true; }
        var quit = ModernTheme.Button("Quit and release memory", (_, _) => Choose(DesktopCloseAction.Quit), true);
        var tray = ModernTheme.Button("Keep running in the tray", (_, _) => Choose(DesktopCloseAction.Tray));
        tray.IsEnabled = trayAvailable;
        body.Children.Add(quit); body.Children.Add(tray);
        if (!trayAvailable) body.Children.Add(ModernTheme.Label("The tray icon isn't available. The window will stay open unless you quit.", 12, ModernTheme.Warning));
        body.Children.Add(ModernTheme.Label("Reopen SayWhat? from its icon near the clock. Right-click that icon to quit later.", 12, ModernTheme.Muted));
        var cancel = ModernTheme.Button("Cancel", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true;
        body.Children.Add(cancel);
        var layers = new Grid();
        layers.Children.Add(ThemeBackground.CreateLayer());
        layers.Children.Add(body);
        dialog.Content = layers;
        dialog.Loaded += (_, _) => cancel.Focus();
        AppearanceTheme.ApplyWindowChrome(dialog);
        UiLocalization.ApplyTree(dialog);
        return dialog;
    }

    internal static int RunOfflineSelfTests()
    {
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Desktop close policy self-check failed."); checks++; }
        Check(ResolveCloseAction("Ask", false, false, false, true) == DesktopCloseAction.Ask);
        Check(ResolveCloseAction("Quit", false, false, false, true) == DesktopCloseAction.Quit);
        Check(ResolveCloseAction("Tray", false, false, false, true) == DesktopCloseAction.Tray);
        Check(ResolveCloseAction("Tray", false, false, false, false) == DesktopCloseAction.Ask);
        Check(ResolveCloseAction("Tray", true, false, false, true) == DesktopCloseAction.Quit);
        Check(ResolveCloseAction("Ask", false, true, false, true) == DesktopCloseAction.Quit);
        Check(ResolveCloseAction("Ask", false, false, true, true) == DesktopCloseAction.Quit);
        Check(ResolveCloseAction("unexpected", false, false, false, true) == DesktopCloseAction.Ask);
        Check(ShouldRemember(new(DesktopCloseAction.Tray, true), false));
        Check(!ShouldRemember(new(DesktopCloseAction.Quit, false), false));
        Check(!ShouldRemember(new(DesktopCloseAction.Cancel, true), false));
        Check(!ShouldRemember(new(DesktopCloseAction.Tray, true), true));
        var saved = new AppSettings { CloseButtonBehavior = "Ask", OwnVoiceTargetLanguage = "Japanese", IncomingTargetLanguage = "English", OwnManagedModelId = "qwen3-8b-q4_k_m" };
        AppSettings updated = WithClosePreference(saved, DesktopCloseAction.Tray);
        Check(saved.CloseButtonBehavior == "Ask" && updated.CloseButtonBehavior == "Tray");
        Check(updated.OwnVoiceTargetLanguage == "Japanese" && updated.IncomingTargetLanguage == "English" && updated.OwnManagedModelId == saved.OwnManagedModelId);
        return checks;
    }
}
