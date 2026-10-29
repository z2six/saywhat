using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace IncomingTranslator;

// Real WPF Closing/dispatcher regressions with inert, isolated owners. Never call Start,
// InspectLocal, an endpoint, a tray API, microphone capture, inference, or a model loader.
internal static class DesktopLifetimeOfflineTests
{
    internal static int RunDispatcherChecks(string isolatedRoot)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Desktop lifetime checks require the desktop test STA.");
        string? configuredRoot = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(AppBrand.LegacyDataEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot) ||
            !string.Equals(Path.GetFullPath(configuredRoot), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Desktop lifetime checks require an initialized isolated test data root.");
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Desktop lifetime dispatcher check failed: " + message);
            checks++;
        }
        void PumpUntil(Func<bool> condition, string message)
        {
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(3)) frame.Continue = false; };
            timer.Start();
            try { if (!condition()) Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Check(condition(), message);
        }
        Window FixtureWindow() => new()
        {
            Title = "SayWhat? offline lifetime fixture", Width = 1, Height = 1,
            Left = -32000, Top = -32000, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Opacity = 0, Content = "Inert test fixture"
        };

        string quitRoot = Path.Combine(Path.GetFullPath(isolatedRoot), "quit-" + Guid.NewGuid().ToString("N"));
        var quitProvisioner = new LocalTalkProvisioner(quitRoot);
        var quitRuntime = new RuntimeController(quitProvisioner);
        Window quitWindow = FixtureWindow();
        var operations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int marked = 0, canceled = 0, closingEvents = 0;
        bool quitClosed = false;
        var quitLifetime = new DesktopLifetime(quitWindow, quitRuntime, quitProvisioner,
            new AppSettings { CloseButtonBehavior = "Quit" }, () => canceled++, () => marked++,
            preview: false, waitForOperations: () => operations.Task, createTrayIcon: false, activateOnRestore: false, enableInstallerShutdown: false);
        quitWindow.Closed += (_, _) => quitClosed = true;
        quitWindow.Closing += (_, _) =>
        {
            closingEvents++;
            if (closingEvents != 1) return;
            Check(marked == 0 && canceled == 0, "quit cleanup has not started inside the original Closing event");
            Task scheduled = quitLifetime.QuitCompletion;
            Check(!scheduled.IsCompleted, "the queued quit completion is published before dispatch");
            quitLifetime.RequestQuit(); quitLifetime.RequestQuit();
            Check(ReferenceEquals(scheduled, quitLifetime.QuitCompletion), "duplicate queued quit requests share one completion task");
        };
        try
        {
            Check(!quitRuntime.OwnRunning && !quitRuntime.IncomingRunning && quitProvisioner.SpeechRegistration is null,
                "the quit fixture has no owned features or registered speech engine");
            quitWindow.Show();
            PumpUntil(() => quitWindow.IsLoaded, "the off-screen quit fixture loaded");
            quitWindow.Close();
            Check(!quitClosed && marked == 0 && canceled == 0 && quitWindow.IsVisible,
                "the first saved-Quit close is canceled while the shutdown operation is queued");
            PumpUntil(() => marked == 1 && canceled == 1, "the queued quit starts after Closing returns");
            Check(!quitClosed && !quitLifetime.QuitCompletion.IsCompleted, "quit waits for the owned background operation to unwind");
            operations.TrySetResult();
            PumpUntil(() => quitClosed && quitLifetime.QuitCompletion.IsCompleted, "the first quit request closes successfully without a retry");
            Check(closingEvents == 2 && marked == 1 && canceled == 1, "one original close and one final close perform callbacks exactly once");
            Check(!quitRuntime.OwnRunning && !quitRuntime.IncomingRunning && !Directory.Exists(quitRoot),
                "the real quit path never creates data, starts a model, or starts capture in the inert fixture");
        }
        finally
        {
            operations.TrySetResult();
            if (!quitClosed)
            {
                quitLifetime.RequestQuit();
                PumpUntil(() => quitClosed, "failed-test cleanup closes its inert quit fixture");
            }
            quitRuntime.DisposeAsync().GetAwaiter().GetResult();
        }

        string hideRoot = Path.Combine(Path.GetFullPath(isolatedRoot), "hide-" + Guid.NewGuid().ToString("N"));
        var hideProvisioner = new LocalTalkProvisioner(hideRoot);
        var hideRuntime = new RuntimeController(hideProvisioner);
        Window hideWindow = FixtureWindow();
        bool hideClosed = false, interceptHide = true;
        var hideLifetime = new DesktopLifetime(hideWindow, hideRuntime, hideProvisioner,
            new AppSettings(), () => { }, () => { }, preview: true, createTrayIcon: false, activateOnRestore: false, enableInstallerShutdown: false);
        hideWindow.Closed += (_, _) => hideClosed = true;
        CancelEventHandler intercept = (_, args) =>
        {
            if (!interceptHide) return;
            args.Cancel = true;
            hideLifetime.DeferHideToTray(); hideLifetime.DeferHideToTray();
            Check(hideWindow.IsVisible, "tray Hide does not run synchronously inside Closing");
        };
        hideWindow.Closing += intercept;
        try
        {
            hideWindow.Show();
            PumpUntil(() => hideWindow.IsLoaded, "the off-screen hide fixture loaded");
            hideWindow.Close();
            Check(hideWindow.IsVisible && !hideClosed, "the original tray close returns before its deferred Hide");
            PumpUntil(() => !hideWindow.IsVisible, "deferred tray Hide completes without a Closing exception");
            Check(!hideClosed && !hideRuntime.OwnRunning && !hideRuntime.IncomingRunning,
                "hiding keeps the dashboard alive without changing an inert runtime");
            hideLifetime.Restore();
            PumpUntil(() => hideWindow.IsVisible, "a hidden dashboard can be restored after Closing unwinds");
            hideLifetime.Restore();
            var restoreDispatched = new DispatcherFrame();
            hideWindow.Dispatcher.BeginInvoke(new Action(() => restoreDispatched.Continue = false), DispatcherPriority.ContextIdle);
            Dispatcher.PushFrame(restoreDispatched);
            Check(hideWindow.IsVisible && !hideClosed, "restoring an already-visible dashboard does not call Show again or close it");
            interceptHide = false;
            hideWindow.Close();
            Check(hideClosed && !Directory.Exists(hideRoot), "the fixture closes without a tray icon or provisioned assets");
        }
        finally
        {
            interceptHide = false;
            hideWindow.Closing -= intercept;
            if (!hideClosed) hideWindow.Close();
            hideRuntime.DisposeAsync().GetAwaiter().GetResult();
        }
        // Installer quit is explicit: it bypasses both Ask and remembered Tray,
        // and does not restore a hidden dashboard just to unload its resources.
        foreach (string preference in new[] { "Ask", "Tray" })
        {
            string installerRoot = Path.Combine(Path.GetFullPath(isolatedRoot), "installer-" + Guid.NewGuid().ToString("N"));
            var provisioner = new LocalTalkProvisioner(installerRoot);
            var runtime = new RuntimeController(provisioner);
            Window window = FixtureWindow();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settings = new AppSettings { CloseButtonBehavior = preference };
            int installerMarked = 0, installerCanceled = 0;
            bool closed = false;
            var lifetime = new DesktopLifetime(window, runtime, provisioner, settings,
                () => installerCanceled++, () => installerMarked++, preview: false,
                waitForOperations: () => finished.Task, createTrayIcon: false, activateOnRestore: false, enableInstallerShutdown: false);
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                PumpUntil(() => window.IsLoaded, "the installer fixture loaded without a real tray or control pipe");
                window.Hide();
                lifetime.RequestInstallerQuit();
                Task completion = lifetime.QuitCompletion;
                lifetime.RequestInstallerQuit();
                Check(ReferenceEquals(completion, lifetime.QuitCompletion), "duplicate installer requests share one graceful cleanup");
                PumpUntil(() => installerMarked == 1 && installerCanceled == 1, "installer quit bypasses " + preference + " without asking");
                Check(!window.IsVisible && !closed && !completion.IsCompleted, "hidden installer cleanup waits without reopening or stealing focus");
                finished.SetResult();
                PumpUntil(() => closed && completion.IsCompleted, "installer-requested quit succeeds on the first attempt");
                Check(settings.CloseButtonBehavior == preference && installerMarked == 1 && installerCanceled == 1,
                    "installer quit preserves the user's close preference and invokes cleanup once");
                Check(!runtime.OwnRunning && !runtime.IncomingRunning && !Directory.Exists(installerRoot),
                    "installer shutdown never provisions assets or starts a model in the inert fixture");
            }
            finally
            {
                finished.TrySetResult();
                if (!closed)
                {
                    lifetime.RequestInstallerQuit();
                    PumpUntil(() => closed, "failed-test cleanup closes its own installer fixture");
                }
                runtime.DisposeAsync().GetAwaiter().GetResult();
            }
        }
        return checks;
    }
}
