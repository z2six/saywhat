namespace IncomingTranslator;

internal sealed class SetupUserException(string message) : InvalidOperationException(message);
internal sealed class ModelSelectionUserException(string message) : ArgumentException(message);
internal static class UserFacingErrors
{
    internal static string Describe(Exception error) => error switch
    {
        SetupUserException => error.Message,
        ModelSelectionUserException => error.Message,
        ManagedPortConfigurationException configuration => DescribePortConfiguration(configuration),
        ManagedPortConflictException conflict => UiText.Format(conflict.Template, UiText.T(conflict.Service), conflict.Port),
        IncomingListeningSelectionException => error.Message,
        PersonPhotoException => error.Message,
        SpeechSetupException => error.Message,
        System.ComponentModel.Win32Exception win32 when win32.NativeErrorCode == 1223 => "Windows setup wasn't approved. Choose Continue setup to try again.",
        HttpRequestException => "Couldn't download the required files. Check your internet connection and try again.",
        UnauthorizedAccessException => "Windows didn't allow access to a required file. Open Troubleshooting for help.",
        TimeoutException => "This is taking longer than expected. Try again or open Troubleshooting.",
        _ => "Couldn't finish this step. Try again or open Troubleshooting for details."
    };

    private static string DescribePortConfiguration(ManagedPortConfigurationException error) =>
        UiText.Format(error.Template, error.Arguments.Select(value => value is string label ? UiText.T(label) : value).ToArray());
}
