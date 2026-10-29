namespace IncomingTranslator;

// Microphone capture and endpoint loopback do not need the newer process-loopback
// API. Never broaden a selected application's audio to desktop audio implicitly.
internal static class IncomingCaptureCapabilities
{
    internal const int ProcessLoopbackMinimumBuild = 20348;
    internal const string UnavailableMessage = "This version of Windows cannot listen to just one application. Your voice translation still works. Choose All playback if you want subtitles from all audio.";
    internal static Version CurrentVersion => Environment.OSVersion.Version;
    internal static bool SupportsProcessLoopback(Version version) => version >= new Version(10, 0, ProcessLoopbackMinimumBuild);
    internal static bool SupportsSource(AppSettings settings, Version version) =>
        !IncomingListening.UsesProcessCapture(settings) || SupportsProcessLoopback(version);
    internal static void Validate(AppSettings settings, Version? version = null)
    {
        if (!SupportsSource(settings, version ?? CurrentVersion))
            throw new IncomingListeningSelectionException(UnavailableMessage);
    }
}
