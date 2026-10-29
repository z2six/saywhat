using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace IncomingTranslator;

internal static class SettingsSaveState
{
    internal static bool HasChanges(AppSettings draft, AppSettings applied, string? selectedProfileId = null) =>
        !TranslationProfiles.ConfigurationEquals(draft, applied)
        || selectedProfileId is not null && selectedProfileId != draft.ActiveTranslationProfileId;

    // A targeted autosave (theme, interface language, Home language or an overlay
    // preference) can coexist with edits elsewhere. Rebase only properties that
    // the draft has not changed since the previous saved snapshot.
    internal static AppSettings Rebase(AppSettings draft, AppSettings previousApplied, AppSettings latestApplied)
    {
        AppSettings result = draft.Copy(), latest = latestApplied.Copy();
        foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite))
        {
            JsonNode? current = JsonSerializer.SerializeToNode(property.GetValue(draft), property.PropertyType);
            JsonNode? previous = JsonSerializer.SerializeToNode(property.GetValue(previousApplied), property.PropertyType);
            if (JsonNode.DeepEquals(current, previous)) property.SetValue(result, property.GetValue(latest));
        }
        return result;
    }
}

internal sealed partial class MainWindow
{
    private AppSettings? _saveStateAppliedSettings;
    private bool _saveStateTrackingInitialized, _saveStateRefreshQueued;

    internal void InitializeSaveStateTracking()
    {
        if (_saveStateTrackingInitialized) return;
        // Install this before the first Navigate, while _settings still contains
        // the snapshot read by the constructor. No second disk read is needed.
        _saveStateAppliedSettings = _settings.Copy();
        _saveStateTrackingInitialized = true;
        // These bubble after the individual control's setter. The queued refresh
        // also observes asynchronous targeted autosaves that finish in that turn.
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, args) =>
        { if (args.OriginalSource is Slider) QueueSaveStateRefresh(); }), true);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        AddHandler(ToggleButton.IndeterminateEvent, new RoutedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => QueueSaveStateRefresh()), true);
        RefreshSaveState();
    }

    private void QueueSaveStateRefresh()
    {
        if (!_saveStateTrackingInitialized || _saveStateRefreshQueued || _busy || _closing
            || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _saveStateRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _saveStateRefreshQueued = false;
            if (!_closing && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) RefreshSaveState();
        }));
    }

    internal bool HasPendingSaveChanges => _saveStateAppliedSettings is not null && SettingsSaveState.HasChanges(_settings, _saveStateAppliedSettings,
        _page == "Settings" && _translationProfilesPicker?.SelectedItem is TranslationProfileUiChoice profile ? profile.Id : null);

    internal void RefreshSaveState(bool reloadSaved = false)
    {
        if (!_saveStateTrackingInitialized) return;
        if (!Dispatcher.CheckAccess())
        { _ = Dispatcher.BeginInvoke(new Action(() => RefreshSaveState(reloadSaved))); return; }
        if (reloadSaved)
        {
            try
            {
                // Load() intentionally falls back to defaults on errors. Do not
                // treat a locked or temporarily unreadable file as a reset.
                AppSettings latest = SettingsStore.ReadSettings(File.ReadAllText(AppPaths.SettingsPath));
                if (_saveStateAppliedSettings is not null)
                    TranslationProfiles.CopyInto(SettingsSaveState.Rebase(_settings, _saveStateAppliedSettings, latest), _settings);
                _saveStateAppliedSettings = latest;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { /* Retain the last known saved snapshot; never clear a real draft. */ }
        }
        bool enabled = !_busy && !_closing && HasPendingSaveChanges;
        // Setup owns a scoped applied-choice snapshot and prerequisite checks.
        // Its sticky header must mirror that primary action, not apply a second
        // (potentially different) settings baseline. Setup itself disables clean
        // Save & apply and keeps missing-file Download & apply actionable.
        if (_page == "Setup" && _settings.FirstRunSetupCompleted && _setupActionButton is not null)
            enabled = !_busy && !_closing && _setupActionButton.IsEnabled;
        _saveButton.IsEnabled = enabled;
        _saveButton.Background = enabled ? ModernTheme.Accent : ModernTheme.Raised;
        _saveButton.Foreground = enabled ? ModernTheme.Ink : ModernTheme.Muted;
        _saveButton.BorderBrush = enabled ? ModernTheme.Accent : ModernTheme.Stroke;
    }
}
