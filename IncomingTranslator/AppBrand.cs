using System.IO;

namespace IncomingTranslator;

// Display branding is separate from filesystem names and compatibility identifiers.
// Windows does not allow '?' in filenames. Never move existing model caches or
// change ownership markers merely to rename the application.
internal static class AppBrand
{
    public const string DisplayName = "SayWhat?";
    public const string FileName = "SayWhat";
    public const string Version = "0.2.6";
    public const string DataEnvironmentVariable = "SAYWHAT_DATA_ROOT";
    public const string LegacyDataEnvironmentVariable = "LOCALTALK_DATA_ROOT";
    public const string DesktopMutex = "Local\\LocalTalkDesktop";
    public const string SpeechDistributionName = "SayWhat-Ubuntu";

    internal static bool IsPackagedAssembly(string? name) => name is FileName or "LocalTalk";

    internal static string ResolveDataRoot(string localAppData, string? configuredRoot = null,
        string? legacyConfiguredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot)) return Path.GetFullPath(configuredRoot);
        if (!string.IsNullOrWhiteSpace(legacyConfiguredRoot)) return Path.GetFullPath(legacyConfiguredRoot);
        string current = Path.Combine(localAppData, FileName);
        string legacy = Path.Combine(localAppData, "LocalTalk");
        // Deterministic preference; never merge unrelated state from two installations.
        return Path.GetFullPath(Directory.Exists(current) ? current : Directory.Exists(legacy) ? legacy : current);
    }

    internal static string DataRoot => ResolveDataRoot(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable(DataEnvironmentVariable),
        Environment.GetEnvironmentVariable(LegacyDataEnvironmentVariable));

    internal static bool IsOwnedSpeechDistributionName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^(SayWhat|LocalTalk)-Ubuntu(-[a-f0-9]{8})?$");
}
