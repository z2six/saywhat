using System.Windows;

namespace IncomingTranslator;

// Reuse the same explicit first-example consent as the person picker. Merely
// seeing a name suggestion or clicking Yes/No never acknowledges it implicitly.
internal static class SpeakerSuggestionReviewUi
{
    internal static async Task<bool> EnsureLearningAcknowledgedAsync(Window owner,
        ISpeakerFeedbackHost host, string personName, bool confirmed)
    {
        if (!owner.Dispatcher.CheckAccess())
            return await owner.Dispatcher.InvokeAsync(() => EnsureLearningAcknowledgedAsync(owner, host, personName, confirmed)).Task.Unwrap();
        SpeakerFeedbackState state = host.SpeakerFeedbackSnapshot ?? await host.GetSpeakerFeedbackStateAsync();
        if (state.InstructionAcknowledged) return true;
        Window dialog = SpeakerFeedbackUi.CreateLearningDialog(owner, personName);
        if (dialog.ShowDialog() != true) return false;
        await host.AcknowledgeSpeakerLearningAsync(CancellationToken.None);
        return true;
    }
}
