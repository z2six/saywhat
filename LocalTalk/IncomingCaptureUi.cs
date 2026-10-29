using System.Windows;
using System.Windows.Controls;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    // An isolated UI test can simulate older Windows without altering the real
    // runtime capability or starting an audio device.
    internal Version? SubtitleWindowsVersionForPreview { get; set; }
    private Version SubtitleWindowsVersion => _preview && SubtitleWindowsVersionForPreview is not null
        ? SubtitleWindowsVersionForPreview : IncomingCaptureCapabilities.CurrentVersion;
    private bool SupportsApplicationSubtitles => IncomingCaptureCapabilities.SupportsProcessLoopback(SubtitleWindowsVersion);
    private bool SupportsSelectedSubtitleSource => IncomingCaptureCapabilities.SupportsSource(_settings, SubtitleWindowsVersion);

    private FrameworkElement CreateIncomingCaptureNotice(string name)
    {
        var notice = SubtitleSpeechLabel(IncomingCaptureCapabilities.UnavailableMessage, 12, ModernTheme.Warning);
        notice.Name = name; return notice;
    }
}
