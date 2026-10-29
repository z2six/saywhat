using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace IncomingTranslator;

/// <summary>Saved audio source identity. A PID is resolved afresh and never saved.</summary>
internal static class IncomingListening
{
    internal const string VrChatMode = "vrchat";
    internal const string ApplicationMode = "application";
    internal const string DesktopMode = "desktop";

    public static string SourceMode(AppSettings settings) => settings.IncomingAudioMode?.Trim().ToLowerInvariant() switch
    {
        VrChatMode => VrChatMode,
        ApplicationMode => ApplicationMode,
        DesktopMode => DesktopMode,
        _ => settings.CaptureVrChatOnly ? VrChatMode : DesktopMode
    };

    public static bool UsesProcessCapture(AppSettings settings) => SourceMode(settings) != DesktopMode;

    // This is an application-supplied label, not a translation key or process-name fallback.
    public static string SourceName(AppSettings settings) => SourceMode(settings) switch
    {
        VrChatMode => "VRChat",
        DesktopMode => "Windows playback",
        _ => !string.IsNullOrWhiteSpace(settings.IncomingApplicationName)
            ? settings.IncomingApplicationName.Trim()
            : ApplicationAudioCatalog.DisplayName(settings.IncomingApplicationPath)
    };

    public static string WaitingMessage(AppSettings settings) => SourceMode(settings) switch
    {
        VrChatMode => "Waiting for speech in VRChat.",
        DesktopMode => "Waiting for playback speech.",
        _ => $"Waiting for {SourceName(settings)}."
    };

    public static bool SameSource(AppSettings left, AppSettings right)
    {
        string mode = SourceMode(left);
        return mode == SourceMode(right) && (mode switch
        {
            ApplicationMode => ApplicationAudioCatalog.SameExecutable(left.IncomingApplicationPath, right.IncomingApplicationPath),
            DesktopMode => string.Equals(left.PlaybackDeviceId, right.PlaybackDeviceId, StringComparison.OrdinalIgnoreCase),
            _ => true
        });
    }

    public static void Normalize(AppSettings settings)
    {
        settings.IncomingAudioMode = SourceMode(settings);
        // Retain the old preference for legacy tools, while all new paths use the mode.
        settings.CaptureVrChatOnly = settings.IncomingAudioMode == VrChatMode;
        settings.IncomingApplicationPath = settings.IncomingApplicationPath?.Trim() ?? "";
        settings.IncomingApplicationName = settings.IncomingApplicationName?.Trim() ?? "";
        settings.IncomingPersonId = settings.IncomingPersonId?.Trim() ?? "";
    }

    public static void Validate(AppSettings settings)
    {
        if (SourceMode(settings) != ApplicationMode) return;
        string path = ApplicationAudioCatalog.CanonicalExecutable(settings.IncomingApplicationPath);
        if (path.Length == 0)
            throw new IncomingListeningSelectionException("Choose an application to translate before starting subtitles.");
        if (ApplicationAudioCatalog.IsBroadLauncher(path))
            throw new IncomingListeningSelectionException("Choose Steam again from the application list to capture its voice and browser audio, not launched games.");
    }

    public static int? TryResolveProcess(AppSettings settings)
    {
        if (SourceMode(settings) == DesktopMode) return null;
        if (SourceMode(settings) == VrChatMode) return ProcessLoopbackCapture.TryFindVrChatProcessId();
        // A vanished or unreadable selection waits; it never changes to all Windows audio.
        string path = ApplicationAudioCatalog.CanonicalExecutable(settings.IncomingApplicationPath);
        if (path.Length == 0 || ApplicationAudioCatalog.IsBroadLauncher(path)) return null;
        IReadOnlyList<ApplicationAudioProcess> snapshot = ApplicationAudioCatalog.Snapshot();
        int? processId = ApplicationAudioCatalog.ResolveProcess(path, snapshot);
        return processId is not null && ApplicationAudioCatalog.IsCurrentSelection(processId.Value, path, snapshot)
            ? processId : null;
    }
}

internal sealed class IncomingListeningSelectionException(string message) : InvalidOperationException(message);

internal sealed record ApplicationAudioChoice(string Path, string Name, int ProcessId)
{
    public override string ToString() => Name;
}

/// <summary>One lightweight process snapshot; fixture instances do not access devices or processes.</summary>
internal sealed record ApplicationAudioProcess(int ProcessId, int ParentProcessId, string Path, string Name,
    bool HasWindow, bool HasAudio, long StartedAtTicks = 0);

internal static class ApplicationAudioCatalog
{
    public static IReadOnlyList<ApplicationAudioChoice> ListRunning() => BuildChoices(Snapshot());

    internal static string CanonicalExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            path = path.Trim();
            if (path.Any(character => char.IsControl(character) || character is '<' or '>' or '|' or '"' or '?' or '*')) return "";
            if (!System.IO.Path.IsPathFullyQualified(path) ||
                !string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return "";
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.IOException)
        { return ""; }
    }

    internal static bool SameExecutable(string? left, string? right)
    {
        string canonical = CanonicalExecutable(left);
        return canonical.Length > 0 && string.Equals(canonical, CanonicalExecutable(right), StringComparison.OrdinalIgnoreCase);
    }

    internal static string DisplayName(string? path)
    {
        string canonical = CanonicalExecutable(path);
        if (canonical.Length == 0) return "selected application";
        string executable = System.IO.Path.GetFileNameWithoutExtension(canonical);
        return executable.ToLowerInvariant() switch
        {
            "discord" => "Discord", "steam" or "steamwebhelper" => "Steam", "vrchat" => "VRChat",
            "chrome" => "Google Chrome", "msedge" => "Microsoft Edge", "firefox" => "Firefox",
            _ => executable
        };
    }

    internal static IReadOnlyList<ApplicationAudioChoice> BuildChoices(IReadOnlyList<ApplicationAudioProcess> processes)
    {
        var byId = processes.Where(process => process.ProcessId > 0).DistinctBy(process => process.ProcessId)
            .ToDictionary(process => process.ProcessId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new List<ApplicationAudioChoice>();
        foreach (ApplicationAudioProcess process in processes.Where(process => process.HasWindow || process.HasAudio || IsSteamVoiceHelper(process.Path)))
        {
            ApplicationAudioProcess root = CaptureRoot(process, byId);
            string path = CanonicalExecutable(root.Path);
            if (path.Length == 0 || IsExcludedHost(path) || !paths.Add(path)) continue;
            int? resolved = ResolveProcess(path, processes);
            if (resolved is not null) choices.Add(new(path, DisplayName(path), resolved.Value));
        }
        return choices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(choice => choice.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static int? ResolveProcess(string? executablePath, IReadOnlyList<ApplicationAudioProcess> processes)
    {
        string path = CanonicalExecutable(executablePath);
        if (path.Length == 0 || IsBroadLauncher(path)) return null;
        var byId = processes.Where(process => process.ProcessId > 0).DistinctBy(process => process.ProcessId)
            .ToDictionary(process => process.ProcessId);
        ApplicationAudioProcess[] roots = processes.Where(process => SameExecutable(path, process.Path))
            .Select(process => SameExecutableRoot(process, byId)).DistinctBy(process => process.ProcessId).ToArray();
        // Prefer a real audio-producing tree, then its visible main window. Deterministic
        // ordering matters when an application has multiple independent instances.
        return roots.OrderByDescending(root => processes.Any(process => process.HasAudio && IsDescendant(process, root, byId)))
            .ThenByDescending(root => root.HasWindow)
            .ThenBy(root => root.StartedAtTicks == 0 ? long.MaxValue : root.StartedAtTicks)
            .ThenBy(root => root.ProcessId).Select(root => (int?)root.ProcessId).FirstOrDefault();
    }

    private static ApplicationAudioProcess CaptureRoot(ApplicationAudioProcess process,
        IReadOnlyDictionary<int, ApplicationAudioProcess> byId) => SameExecutableRoot(process, byId);

    // Steam launches games below its main process. Its Chromium helper tree owns
    // the client/chat audio, so selecting that narrower tree avoids the game branch.
    internal static bool IsBroadLauncher(string path) =>
        string.Equals(System.IO.Path.GetFileName(path), "steam.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsSteamVoiceHelper(string path) =>
        string.Equals(System.IO.Path.GetFileName(path), "steamwebhelper.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedHost(string path) => System.IO.Path.GetFileName(path).ToLowerInvariant() is
        "explorer.exe" or "dwm.exe" or "audiodg.exe" or "svchost.exe" or "system" or "saywhat.exe" or "steam.exe";

    private static ApplicationAudioProcess SameExecutableRoot(ApplicationAudioProcess process,
        IReadOnlyDictionary<int, ApplicationAudioProcess> byId)
    {
        var seen = new HashSet<int> { process.ProcessId };
        while (TryParent(process, byId, out ApplicationAudioProcess? parent) &&
            SameExecutable(process.Path, parent!.Path) && seen.Add(parent.ProcessId)) process = parent;
        return process;
    }

    private static bool IsDescendant(ApplicationAudioProcess process, ApplicationAudioProcess root,
        IReadOnlyDictionary<int, ApplicationAudioProcess> byId)
    {
        var seen = new HashSet<int>();
        while (seen.Add(process.ProcessId))
        {
            if (process.ProcessId == root.ProcessId) return true;
            if (!TryParent(process, byId, out ApplicationAudioProcess? parent)) return false;
            process = parent!;
        }
        return false;
    }

    private static bool TryParent(ApplicationAudioProcess process, IReadOnlyDictionary<int, ApplicationAudioProcess> byId,
        out ApplicationAudioProcess? parent)
    {
        parent = null;
        if (process.ParentProcessId <= 0 || !byId.TryGetValue(process.ParentProcessId, out ApplicationAudioProcess? found)) return false;
        // Windows may reuse the dead parent's PID. A later process cannot be its parent.
        if (process.StartedAtTicks > 0 && found.StartedAtTicks > process.StartedAtTicks) return false;
        parent = found;
        return true;
    }

    internal static IReadOnlyList<ApplicationAudioProcess> Snapshot()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ApplicationAudioProcess>();
        HashSet<int> audio = AudioProcessIds();
        Dictionary<int, int> parents = ParentProcessIds();
        var result = new List<ApplicationAudioProcess>();
        int ownSession;
        using (Process current = Process.GetCurrentProcess()) ownSession = current.SessionId;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != ownSession) continue;
                    string path = ReadExecutable(process.Id);
                    if (path.Length == 0) continue; // A protected process is not a safe selection.
                    long started = 0;
                    try { started = process.StartTime.ToUniversalTime().Ticks; } catch (System.ComponentModel.Win32Exception) { }
                    result.Add(new(process.Id, parents.GetValueOrDefault(process.Id), path, DisplayName(path),
                        process.MainWindowHandle != IntPtr.Zero, audio.Contains(process.Id), started));
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
                { /* Processes can disappear or deny metadata while the picker is open. */ }
            }
        }
        return result;
    }

    private static HashSet<int> AudioProcessIds()
    {
        var result = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        SessionCollection sessions = device.AudioSessionManager.Sessions;
                        for (int index = 0; index < sessions.Count; index++)
                        {
                            using AudioSessionControl session = sessions[index];
                            uint processId = session.GetProcessID;
                            if (processId > 0 && processId <= int.MaxValue) result.Add((int)processId);
                        }
                    }
                    catch (Exception exception) when (exception is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { /* A removed endpoint/session does not invalidate other candidates. */ }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Visible application windows remain available when no output device exists. */ }
        return result;
    }

    internal static bool IsCurrentSelection(int processId, string path, IReadOnlyList<ApplicationAudioProcess> snapshot)
    {
        if (!SameExecutable(path, ReadExecutable(processId))) return false;
        long expectedStart = snapshot.FirstOrDefault(process => process.ProcessId == processId)?.StartedAtTicks ?? 0;
        if (expectedStart == 0) return true;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == expectedStart;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static string ReadExecutable(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(0x1000, false, (uint)processId); // QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return "";
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) ? CanonicalExecutable(path.ToString()) : "";
    }

    private static Dictionary<int, int> ParentProcessIds()
    {
        var parents = new Dictionary<int, int>();
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return parents;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) return parents;
        do
        {
            if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
        } while (Process32Next(snapshot, ref entry));
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
