namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    // User feedback and late identity callbacks are serialized, independently of
    // text/audio workers. A slow metadata write never gates a translation.
    private readonly SemaphoreSlim _speakerReviewChanges = new(1, 1);
    private readonly Dictionary<long, (string VoiceKey, string PersonId)> _groupRejectedSuggestions = new();

    public async Task<SpeakerFeedbackResult> ReviewSpeakerSuggestionAsync(string evidenceId,
        string suggestedPersonId, bool confirmed, CancellationToken token = default)
    {
        await _speakerReviewChanges.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_feedbackGate)
            {
                if (_disposed || !_evidenceBubbles.TryGetValue(evidenceId, out long id) ||
                    !_publishedDisplays.TryGetValue(id, out TranslationDisplay display))
                    return new(false, "This suggestion is no longer available.", null, "Unidentified speaker", false, false);
                display = ResolveDisplayIdentityLocked(display);
                if (display.IsManuallyAttributed || display.EvidenceId != evidenceId ||
                    display.PersonId != suggestedPersonId || !IsRecognitionAttributionCurrent(display.SpeakerRecognitionEpoch))
                    return new(false, "The suggested person changed. Review the current name instead.", null, "Unidentified speaker", false, false);
            }
            _aiSpeakerIdentifier.RegisterSpeakerSuggestion(evidenceId, suggestedPersonId);
            SpeakerFeedbackResult result = await _aiSpeakerIdentifier.ReviewSpeakerSuggestionAsync(
                evidenceId, suggestedPersonId, confirmed, token).ConfigureAwait(false);
            ApplyContextCorrection(evidenceId, result);
            await UpdateCorrectedTranslationCountAsync(evidenceId, result, token).ConfigureAwait(false);
            Log("Person suggestion reviewed: " + result.Message);
            return result;
        }
        finally { _speakerReviewChanges.Release(); }
    }

    private void RegisterCurrentSpeakerSuggestion(string evidenceId)
    {
        string? personId = null;
        lock (_feedbackGate)
        {
            if (_evidenceBubbles.TryGetValue(evidenceId, out long id) &&
                _publishedDisplays.TryGetValue(id, out TranslationDisplay display))
            {
                display = ResolveDisplayIdentityLocked(display);
                if (!display.IsManuallyAttributed && display.EvidenceId == evidenceId) personId = display.PersonId;
            }
        }
        if (personId is not null) _aiSpeakerIdentifier.RegisterSpeakerSuggestion(evidenceId, personId);
    }

    private bool IsConfirmedDisplayLocked(long bubbleId) =>
        _manualSpeakers.TryGetValue(bubbleId, out SpeakerFeedbackResult? correction)
            ? correction.IsManuallyAttributed : HasSinglePersonSelectionLocked() || IsManualGroupAttributionLocked(bubbleId);

    private static string LiveSpeakerLabel(SpeakerAttribution speaker) =>
        speaker.PersonId is not null && !speaker.PersonConfirmed
            ? GroupSpeechProtocol.VoiceLabel(speaker.SessionVoiceKey) : speaker.Label;

    private bool IsGroupSuggestionRejectedLocked(string voiceKey, string? personId) => personId is not null &&
        _groupRejectedSuggestions.Values.Any(item => item.VoiceKey == voiceKey && item.PersonId == personId);

    private SpeakerAttribution SuppressRejectedGroupSuggestionLocked(SpeakerAttribution speaker)
    {
        if (!IsGroupSuggestionRejectedLocked(speaker.SessionVoiceKey, speaker.PersonId)) return speaker;
        return speaker with { PersonId = null, SuggestedPersonId = null, PersonConfirmed = false,
            Confidence = 0, Label = GroupSpeechProtocol.VoiceLabel(speaker.SessionVoiceKey) };
    }
}
