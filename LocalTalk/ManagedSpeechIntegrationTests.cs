namespace IncomingTranslator;

internal static class ManagedSpeechIntegrationTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Speech app integration: " + message); checks++; }
        Guid id = Guid.Parse("dde0e955-bbde-4907-9d96-3a2ee359f9ee");
        string distro = "SayWhat-Ubuntu";
        string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL");
        ManagedSpeechIntegration.Shortcut shell = new(Path.Combine(wsl, "wsl.exe"), "--distribution-id " + id.ToString("B") + " --cd ~");
        ManagedSpeechIntegration.Shortcut profiler = new(Path.Combine(wsl, "wslg.exe"), "-d SayWhat-Ubuntu --cd \"~\" -- /usr/local/cuda-13.1/bin/nvvp");
        Check(ManagedSpeechIntegration.IsGeneratedShortcut(distro + ".lnk", shell, distro, id), "recognizes the exact registered internal shell");
        Check(ManagedSpeechIntegration.IsGeneratedShortcut("NVIDIA Visual Profiler (SayWhat-Ubuntu).lnk", profiler, distro, id), "recognizes verified toolkit desktop shortcut");
        foreach (var sample in new[]
        {
            ("SayWhat.lnk", shell, distro, id), ("SayWhat-Ubuntu.lnk", shell, "Ubuntu", id),
            (distro + ".lnk", shell, distro, Guid.NewGuid()),
            (distro + ".lnk", shell with { Target = @"C:\untrusted\wsl.exe" }, distro, id),
            (distro + ".lnk", shell with { Arguments = shell.Arguments + " --exec injected" }, distro, id),
            ("NVIDIA Visual Profiler (SayWhat-Ubuntu).lnk", profiler with { Arguments = profiler.Arguments + "; echo injected" }, distro, id),
            ("User tool (SayWhat-Ubuntu).lnk", profiler, distro, id),
            ("NVIDIA Visual Profiler (SayWhat-Ubuntu).lnk", profiler with { Arguments = "-d Ubuntu --cd \"~\" -- /usr/local/cuda-13.1/bin/nvvp" }, distro, id)
        }) Check(!ManagedSpeechIntegration.IsGeneratedShortcut(sample.Item1, sample.Item2, sample.Item3, sample.Item4), "keeps unrelated or modified shortcut");
        foreach (string name in new[] { "SayWhat-Ubuntu", "SayWhat-Ubuntu-0123abcd", "LocalTalk-Ubuntu-abcdef12" })
            Check(ManagedSpeechIntegration.IsManagedRegistration(new(name,
                "/opt/localtalk-voxtral/3d1ed513debc88b4a383ad83e0cd0bcb84889a60-localtalk-r2/build-cuda/voxtral-server", "/synthetic/model.gguf", "cuda") { RunAsRoot = true }),
                "keeps legacy and suffixed managed distro names compatible");
        Check(!ManagedSpeechIntegration.IsManagedRegistration(null), "missing registration does nothing");
        Check(!ManagedSpeechIntegration.IsManagedRegistration(new(distro, "/opt/user/server", "/user/model", "cuda") { RunAsRoot = true }), "name alone is not ownership proof");
        Check(!ManagedSpeechIntegration.IsManagedRegistration(new("Ubuntu", "/opt/user/server", "/user/model", "cuda")), "never modifies user's Ubuntu");

        using (var provisioner = new LocalTalkProvisioner(Path.Combine(directory, "no-written-preferences")))
        {
            WslSpeechRegistration owned = new(distro,
                "/opt/localtalk-voxtral/3d1ed513debc88b4a383ad83e0cd0bcb84889a60-localtalk-r2/build-cuda/voxtral-server",
                "/synthetic/model.gguf", "cuda") { RunAsRoot = true };
            var managedStart = provisioner.BuildWslSpeechStartInfo(owned, 8080, "0123456789abcdef0123456789abcdef");
            Check(managedStart.ArgumentList.Last() == "--hide-desktop-integration", "requested managed speech start migrates metadata without requiring setup again");
            foreach (WslSpeechRegistration imported in new[] { owned with { Distribution = "Ubuntu" },
                owned with { RunAsRoot = false }, owned with { ServerPath = "/opt/user/voxtral-server" } })
                Check(!provisioner.BuildWslSpeechStartInfo(imported, 8080, "0123456789abcdef0123456789abcdef").ArgumentList.Contains("--hide-desktop-integration"),
                    "imported or non-owned speech starts never modify desktop metadata");
            foreach (int port in new[] { 1024, 25347, 65535 })
            {
                string expected = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Check(provisioner.BuildWslSpeechStartInfo(owned, port, "0123456789abcdef0123456789abcdef").ArgumentList.Contains(expected),
                    "WSL start passes a safe selected port without shell interpolation");
                Check(provisioner.BuildWslSpeechStopInfo(owned, port, "0123456789abcdef0123456789abcdef").ArgumentList.TakeLast(2)
                    .SequenceEqual(new[] { expected, "0123456789abcdef0123456789abcdef" }),
                    "WSL cleanup retains the exact selected port and owner token");
            }
            foreach (int port in new[] { -1, 0, 1023, 65536, int.MaxValue })
            {
                foreach (Action construct in new Action[] {
                    () => provisioner.BuildWslSpeechStartInfo(owned, port, "0123456789abcdef0123456789abcdef"),
                    () => provisioner.BuildWslSpeechStopInfo(owned, port, "0123456789abcdef0123456789abcdef") })
                {
                    bool refused = false;
                    try { construct(); } catch (ArgumentOutOfRangeException) { refused = true; }
                    Check(refused, "invalid WSL start/stop ports fail before any process or filesystem work");
                }
            }
            Check(!Directory.Exists(provisioner.DataRoot), "start-info construction starts no processes or filesystem changes");
        }

        string startMenu = Path.Combine(directory, "menu"), programs = Path.Combine(startMenu, "Programs");
        string guiRoot = Path.Combine(programs, distro), backup = Path.Combine(directory, "backups");
        Directory.CreateDirectory(guiRoot);
        string shellPath = Path.Combine(startMenu, distro + ".lnk"), guiPath = Path.Combine(guiRoot, "NVIDIA Visual Profiler (SayWhat-Ubuntu).lnk");
        string unrelated = Path.Combine(guiRoot, "Keep me.lnk"), mainApp = Path.Combine(programs, "SayWhat.lnk");
        File.WriteAllText(shellPath, "fixture shell"); File.WriteAllText(guiPath, "fixture profiler");
        File.WriteAllText(unrelated, "unrelated fixture"); File.WriteAllText(mainApp, "main app fixture");
        ManagedSpeechIntegration.Shortcut? Read(string path) => path == shellPath ? shell : path == guiPath ? profiler : null;
        Check(ManagedSpeechIntegration.HideVerifiedShortcuts(distro, id, startMenu, programs, backup, Read) == 2, "quarantines only matching fixture shortcuts");
        Check(File.Exists(unrelated) && File.Exists(mainApp) && Directory.Exists(guiRoot), "keeps app shortcut, unfamiliar files and directories");
        string[] saved = Directory.GetFiles(backup, "*.lnk", SearchOption.AllDirectories);
        Check(saved.Length == 2 && saved.Select(File.ReadAllText).OrderBy(x => x).SequenceEqual(new[] { "fixture profiler", "fixture shell" }), "backup bytes are recoverable unchanged");
        Check(ManagedSpeechIntegration.HideVerifiedShortcuts(distro, id, startMenu, programs, backup, Read) == 0, "repeat cleanup is harmless");
        Check(ManagedSpeechIntegration.HideVerifiedShortcuts("Ubuntu", id, startMenu, programs, backup, Read) == 0, "foreign distro creates no backup or changes");
        return checks;
    }
}
