using System.Text.RegularExpressions;

namespace IncomingTranslator;

// Keep the base app separate from optional Linux/GPU and process-audio APIs.
// These are compatibility gates, not evidence of live testing on every build.
internal static class WindowsSpeechRequirements
{
    internal const int BaseMinimumBuild = 17763;
    internal const int WslMinimumBuild = 19041;
    internal const int WslGpuMinimumBuild = 19044;
    // Microsoft introduced general.guiApplications in the released WSL 0.48.2.
    internal static readonly Version MinimumHeadlessWsl = new(0, 48, 2);
    internal const string WslWindowsMessage = "This speech option needs Windows 10 version 2004 or newer. Choose Nemotron or FireRed for speech recognition on this Windows version.";
    internal const string WslGpuWindowsMessage = "GPU speech recognition in Linux needs Windows 10 version 21H2 or newer. Choose a Windows speech model, or update Windows before preparing this option.";
    internal const string WslUpdateMessage = "Update WSL before preparing this Linux speech option, or choose a Windows speech model.";

    internal static void Validate(string backend, Version? windows = null)
    {
        Version current = windows ?? Environment.OSVersion.Version;
        if (current < new Version(10, 0, WslMinimumBuild)) throw new SetupUserException(WslWindowsMessage);
        if ((backend is "cuda" or "vulkan") && current < new Version(10, 0, WslGpuMinimumBuild))
            throw new SetupUserException(WslGpuWindowsMessage);
    }

    internal static Version? ParseWslVersion(string text)
    {
        string? first = text.Replace("\0", "", StringComparison.Ordinal).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (first is null || first.Length > 1024) return null;
        Match value = Regex.Match(first, @"(?<![\d.])(?<version>\d{1,3}\.\d{1,3}\.\d{1,5}(?:\.\d{1,5})?)(?![\d.])");
        return value.Success && Version.TryParse(value.Groups["version"].Value, out Version? version) ? version : null;
    }

    internal static bool SupportsHeadlessWsl(string output) => ParseWslVersion(output) is Version version && version >= MinimumHeadlessWsl;
}

internal static class WindowsSpeechRequirementTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        bool Allowed(string backend, int build)
        {
            try { WindowsSpeechRequirements.Validate(backend, new Version(10, 0, build)); return true; }
            catch (SetupUserException) { return false; }
        }
        Check(!Allowed("cpu", 17763) && !Allowed("cpu", 19040), "Older app-compatible Windows builds cannot start automatic WSL setup.");
        Check(Allowed("cpu", 19041) && !Allowed("cuda", 19043) && !Allowed("vulkan", 19043), "WSL and GPU speech use separate Windows feature floors.");
        Check(Allowed("cuda", 19044) && Allowed("vulkan", 19045) && Allowed("cuda", 22000), "Newer Windows versions retain optional GPU speech.");
        foreach (string output in new[] { "WSL version: 2.6.3.0\nKernel version: 6.6.87", "WSL 版本： 2.7.11.0\r\n", "WSL-Version: 3.0.1.0", "WSL バージョン: 2.6.3", "WSL version: 1.0.0.0", "WSL version: 0.48.2.0" })
            Check(WindowsSpeechRequirements.SupportsHeadlessWsl(output), "Localized modern WSL metadata is accepted without launching a distro.");
        foreach (string output in new[] { "WSL version: 0.47.1.0\nKernel version: 6.6.87", "", "Kernel version: 0.0.0", "WSL version: 0.48.1.99" })
            Check(!WindowsSpeechRequirements.SupportsHeadlessWsl(output), "Missing or older WSL metadata cannot claim headless-image support.");
        return checks;
    }
}
