namespace IncomingTranslator;

internal static class IncomingProfilePipelineTests
{
    internal static Task<int> RunAsync(string directory) => IncomingTranslationService.RunProfilePipelineOfflineChecksAsync(directory);
}
