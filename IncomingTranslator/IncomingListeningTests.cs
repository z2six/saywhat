using System.Text.Json;

namespace IncomingTranslator;

/// <summary>Pure source-selection fixtures: no audio devices, processes, models or user-data writes.</summary>
internal static class IncomingListeningTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }
        void InvalidSelection(string path)
        {
            bool rejected = false;
            try { IncomingListening.Validate(new() { IncomingAudioMode = "application", IncomingApplicationPath = path }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, $"An invalid application identity is rejected without desktop fallback: {path}");
        }

        var defaults = new AppSettings();
        Check(IncomingListening.SourceMode(defaults) == "vrchat" && IncomingListening.UsesProcessCapture(defaults),
            "The fresh default preserves application-only VRChat capture");
        Check(IncomingListening.SourceMode(new() { CaptureVrChatOnly = false }) == "desktop",
            "The previous explicit Windows-playback choice migrates without changing its scope");
        var legacyVrChat = SettingsStore.ReadSettings("{\"CaptureVrChatOnly\":true}");
        var legacyDesktop = SettingsStore.ReadSettings("{\"CaptureVrChatOnly\":false}");
        Check(legacyVrChat.IncomingAudioMode == "vrchat" && legacyDesktop.IncomingAudioMode == "desktop",
            "Persisted pre-mode settings migrate to their actual previous capture source");
        var application = SettingsStore.Normalize(new()
        {
            IncomingAudioMode = " Application ", CaptureVrChatOnly = true,
            IncomingApplicationPath = @" C:\Apps\Discord\Discord.exe ", IncomingApplicationName = " Discord ",
            IncomingSinglePerson = true, IncomingPersonId = " person-alice "
        });
        Check(application.IncomingAudioMode == "application" && !application.CaptureVrChatOnly &&
            application.IncomingApplicationPath == @"C:\Apps\Discord\Discord.exe" && application.IncomingPersonId == "person-alice",
            "The explicit application source wins over the legacy flag and retains its exact path and chosen person");
        Check(IncomingListening.SourceName(application) == "Discord" && IncomingListening.WaitingMessage(application) == "Waiting for Discord.",
            "The selected application has a useful waiting message without using its process name as identity");
        Check(IncomingListening.SourceName(new() { IncomingAudioMode = "application", IncomingApplicationPath = @"C:\Apps\steam.exe" }) == "Steam",
            "Older unnamed selections derive only their display label from the saved executable path");
        Check(!IncomingListening.UsesProcessCapture(legacyDesktop) && IncomingListening.SourceName(legacyDesktop) == "Windows playback",
            "Explicit desktop mode is distinguishable and does not claim a process-only source");
        Check(IncomingListening.SourceMode(new() { IncomingAudioMode = "unknown", CaptureVrChatOnly = true }) == "vrchat" &&
            IncomingListening.SourceMode(new() { IncomingAudioMode = null!, CaptureVrChatOnly = false }) == "desktop",
            "Unknown or absent source modes have a bounded compatibility fallback");
        AppSettings copied = application.Copy();
        Check(copied.IncomingAudioMode == application.IncomingAudioMode && copied.IncomingApplicationPath == application.IncomingApplicationPath &&
            copied.IncomingApplicationName == "Discord" && copied.IncomingSinglePerson && copied.IncomingPersonId == "person-alice",
            "Copying settings preserves application mode, stable identity and manual one-person selection");
        AppSettings saved = SettingsStore.ReadSettings(JsonSerializer.Serialize(application));
        Check(saved.IncomingAudioMode == "application" && saved.IncomingApplicationPath == application.IncomingApplicationPath &&
            saved.IncomingSinglePerson && saved.IncomingPersonId == "person-alice",
            "A normal persisted settings round-trip preserves the separate listening mode");
        AppSettings custom = TranslationProfiles.Create(application, "Discord with Alice");
        AppSettings changed = custom.Copy();
        changed.IncomingAudioMode = "vrchat";
        changed.IncomingSinglePerson = false;
        changed.IncomingPersonId = "";
        AppSettings restored = TranslationProfiles.Select(changed, custom.ActiveTranslationProfileId);
        Check(restored.IncomingAudioMode == "application" && restored.IncomingApplicationPath == application.IncomingApplicationPath &&
            restored.IncomingSinglePerson && restored.IncomingPersonId == "person-alice",
            "Custom profiles capture and restore the chosen application and one-person mode");
        AppSettings normal = TranslationProfiles.Select(application, TranslationProfiles.NormalId);
        Check(normal.IncomingAudioMode == "application" && normal.IncomingApplicationPath == application.IncomingApplicationPath &&
            normal.IncomingSinglePerson && normal.IncomingPersonId == "person-alice",
            "A speed/profile selection does not unexpectedly replace the audio source or manual person");
        IncomingListening.Validate(application);
        Check(true, "A stopped or uninstalled saved application may wait without requiring file existence");
        foreach (string path in new[] { "", "Discord.exe", @"Apps\Discord.exe", @"C:\Apps\Discord.dll", @"C:\Apps\*.exe", @"C:\Apps\Bad\" + '\0' + "x.exe" })
            InvalidSelection(path);
        InvalidSelection(@"C:\Games\Steam\Steam.exe");
        Check(IncomingListening.SameSource(application, new() { IncomingAudioMode = "application", IncomingApplicationPath = @"c:\APPS\discord\.\discord.EXE", IncomingApplicationName = "Renamed" }),
            "Source comparison uses canonical case-insensitive executable paths, not labels or PIDs");
        Check(!IncomingListening.SameSource(application, new() { IncomingAudioMode = "application", IncomingApplicationPath = @"D:\Apps\Discord\Discord.exe" }) &&
            !IncomingListening.SameSource(application, legacyDesktop) && IncomingListening.SameSource(legacyVrChat, new()),
            "Different installations and capture scopes remain distinct");
        Check(!IncomingListening.SameSource(new() { IncomingAudioMode = "desktop", PlaybackDeviceId = "default" },
            new() { IncomingAudioMode = "desktop", PlaybackDeviceId = "another-device" }) &&
            IncomingListening.SameSource(new() { IncomingAudioMode = "vrchat", PlaybackDeviceId = "default" },
            new() { IncomingAudioMode = "vrchat", PlaybackDeviceId = "another-device" }),
            "Desktop device changes renew capture while process-only sources remain independent of endpoint selection");

        const string discord = @"C:\Apps\Discord\Discord.exe";
        const string otherDiscord = @"D:\Apps\Discord\Discord.exe";
        const string steam = @"C:\Games\Steam\steam.exe";
        const string steamHelper = @"C:\Games\Steam\bin\cef\steamwebhelper.exe";
        ApplicationAudioProcess[] processes =
        [
            new(1, 0, @"C:\Windows\explorer.exe", "Windows shell", true, false, 1),
            new(10, 1, discord, "Discord", true, false, 10),
            new(11, 10, discord, "Discord renderer", false, true, 11),
            new(12, 11, discord, "Discord audio utility", false, true, 12),
            new(20, 1, otherDiscord, "Discord other copy", true, false, 20),
            new(30, 1, steam, "Steam", true, false, 30),
            new(31, 30, steamHelper, "Steam web helper", false, true, 31),
            new(40, 1, @"C:\Apps\SilentWorker.exe", "Background worker", false, false, 40),
            new(50, 1, "", "Protected process", true, true, 50)
        ];
        Check(ApplicationAudioCatalog.ResolveProcess(discord, processes) == 10,
            "Discord resolves to the same-executable tree root so renderer/audio children are captured together");
        Check(ApplicationAudioCatalog.ResolveProcess(@"c:\Apps\Discord\.\DISCORD.exe", processes) == 10,
            "Equivalent canonical executable paths resolve to the same process tree");
        Check(ApplicationAudioCatalog.ResolveProcess(otherDiscord, processes) == 20 &&
            ApplicationAudioCatalog.ResolveProcess(@"E:\Apps\Discord\Discord.exe", processes) is null,
            "A same-name executable from another directory cannot replace the selected application");
        Check(ApplicationAudioCatalog.ResolveProcess("Discord", processes) is null,
            "A bare process name never becomes an application-capture fallback");
        var choices = ApplicationAudioCatalog.BuildChoices(processes);
        Check(choices.Count == 3 && choices.Any(choice => choice.Path == discord && choice.ProcessId == 10) &&
            choices.Any(choice => choice.Path == otherDiscord && choice.ProcessId == 20),
            "The picker includes windowed and audio-producing applications and keeps distinct exact-path installations");
        Check(choices.Any(choice => choice.Path == steamHelper && choice.ProcessId == 31 && choice.Name == "Steam") &&
            choices.All(choice => choice.Path != steam) && ApplicationAudioCatalog.ResolveProcess(steam, processes) is null,
            "Steam selects its narrow browser/voice helper tree, never the launcher tree that owns launched games");
        Check(ApplicationAudioCatalog.BuildChoices(new ApplicationAudioProcess[]
            { new(30, 1, steam, "Steam", true, false, 30), new(31, 30, steamHelper, "Silent Steam helper", false, false, 31) })
            .Single().ProcessId == 31,
            "Steam remains selectable before a voice call opens an audio session, without capturing its launcher");
        ApplicationAudioProcess[] steamWithGame = processes.Concat(new[]
        {
            new ApplicationAudioProcess(32, 30, @"C:\Games\VRChat\VRChat.exe", "Launched game", true, true, 32),
            new ApplicationAudioProcess(33, 31, steamHelper, "Steam audio renderer", false, true, 33)
        }).ToArray();
        Check(ApplicationAudioCatalog.ResolveProcess(steamHelper, steamWithGame) == 31 &&
            ApplicationAudioCatalog.BuildChoices(steamWithGame).Single(choice => choice.Name == "Steam").ProcessId == 31 &&
            ApplicationAudioCatalog.BuildChoices(steamWithGame).Any(choice => choice.Name == "VRChat" && choice.ProcessId == 32),
            "The selected Steam audio tree includes its renderer but not a launched game's sibling branch");
        Check(choices.All(choice => choice.Path.Length > 0 && !choice.Path.Contains("SilentWorker", StringComparison.Ordinal) &&
            !choice.Path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)),
            "Protected/background/shell processes do not turn into broad audio-source choices");
        Check(ApplicationAudioCatalog.ResolveProcess(discord, processes.Where(process => process.ProcessId is not (10 or 11 or 12)).ToArray()) is null,
            "A disappearing selected application waits instead of capturing another application's audio");
        ApplicationAudioProcess[] restarted = [new(200, 1, discord, "Discord restarted", true, true, 200)];
        Check(ApplicationAudioCatalog.ResolveProcess(discord, restarted) == 200,
            "Restarted applications resolve by stable executable path rather than a stale saved PID");
        ApplicationAudioProcess[] reusedParent = [new(100, 0, discord, "New process", true, false, 100), new(101, 100, discord, "Orphaned older child", false, true, 50)];
        Check(ApplicationAudioCatalog.ResolveProcess(discord, reusedParent) == 101,
            "A reused newer PID cannot incorrectly adopt an older audio process as its child");
        ApplicationAudioProcess[] twoTrees = [new(100, 0, discord, "Idle window", true, false, 100), new(200, 0, discord, "Audible instance", false, false, 200), new(201, 200, discord, "Audible child", false, true, 201)];
        Check(ApplicationAudioCatalog.ResolveProcess(discord, twoTrees) == 200,
            "An independently running audible tree takes priority over an idle main window with the same path");
        ApplicationAudioProcess[] cycle = [new(80, 81, discord, "Cycle A", true, false), new(81, 80, discord, "Cycle B", false, true)];
        Check(ApplicationAudioCatalog.ResolveProcess(discord, cycle) is 80 or 81 && ApplicationAudioCatalog.BuildChoices(cycle).Count == 1,
            "Malformed parent cycles remain bounded rather than hanging source discovery");
        ApplicationAudioProcess[] unrelatedHelper = [new(300, 1, @"C:\Another\steam.exe", "Unrelated Steam", false, false, 300), new(301, 300, steamHelper, "Unrelated helper", false, true, 301)];
        Check(ApplicationAudioCatalog.BuildChoices(unrelatedHelper).All(choice => choice.Path != @"C:\Another\steam.exe") &&
            ApplicationAudioCatalog.ResolveProcess(steamHelper, unrelatedHelper) == 301,
            "An unrelated Steam-named ancestor cannot broaden the narrow helper capture selection");
        return checks;
    }
}
