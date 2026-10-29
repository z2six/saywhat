using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

internal static class SpeakerFeedbackUi
{
    internal const string MusicLabel = "music";
    internal const string UnidentifiedLabel = "unidentified";

    public static async Task<SpeakerFeedbackResult?> ShowCorrectionAsync(Window owner, ISpeakerFeedbackHost host, TranslationDisplay sentence)
    {
        if (!owner.Dispatcher.CheckAccess())
            return await owner.Dispatcher.InvokeAsync(() => ShowCorrectionAsync(owner, host, sentence)).Task.Unwrap();
        SpeakerFeedbackState state = await host.GetSpeakerFeedbackStateAsync();
        Window dialog = CreateCorrectionDialog(owner, host, sentence, state);
        return dialog.ShowDialog() == true ? dialog.Tag as SpeakerFeedbackResult : null;
    }

    internal static Window CreateCorrectionDialog(Window owner, ISpeakerFeedbackHost host,
        TranslationDisplay sentence, SpeakerFeedbackState state)
    {
        Window dialog = Shell(owner, "Name this person", 580);
        var body = new StackPanel();
        body.Children.Add(ModernTheme.Heading("Who said this?", 26));
        body.Children.Add(ModernTheme.Label("Choose a person, or mark this as music or an unidentified voice.", 13, ModernTheme.Muted));
        var excerpt = new StackPanel();
        excerpt.Children.Add(ModernTheme.Label(sentence.Translation, 16, localize:false));
        excerpt.Children.Add(ModernTheme.Label(sentence.Source, 12, ModernTheme.Muted, localize:false));
        var excerptScroll = new ScrollViewer { Content = excerpt, MaxHeight = 145, Padding = new Thickness(0, 0, 10, 0) };
        body.Children.Add(ModernTheme.Card(excerptScroll));
        var choices = state.People.Select(person => new CorrectionChoice(person.Name, person.Id)).ToList();
        choices.Add(new(UiText.T("Create a person…"), Create: true));
        choices.Add(new(UiText.T("Music / recording · label only"), Special: MusicLabel));
        choices.Add(new(UiText.T("Unidentified voice · label only"), Special: UnidentifiedLabel));
        var selection = new ComboBox { ItemsSource = choices, MinHeight = 42, Margin = new Thickness(0, 4, 0, 10) };
        UiLocalization.Bind(selection, System.Windows.Automation.AutomationProperties.NameProperty, "Choose the person");
        selection.SelectedItem = choices.FirstOrDefault(choice => choice.PersonId is not null && choice.PersonId == sentence.PersonId);
        body.Children.Add(selection);
        var newPerson = new StackPanel { Visibility = Visibility.Collapsed };
        newPerson.Children.Add(ModernTheme.Label("New person's name", 12, ModernTheme.Muted));
        var name = new TextBox { MaxLength = 64, MinHeight = 40, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(name,"New person's name");
        newPerson.Children.Add(name); body.Children.Add(newPerson);
        body.Children.Add(ModernTheme.Label("A clear example helps recognize this person next time. If it is too short, unclear or too old, only the name on this sentence changes. Audio recordings are not saved.", 12, ModernTheme.Muted));
        var status = ModernTheme.Label("", 12, ModernTheme.Warning); body.Children.Add(status);
        var actions = new WrapPanel();
        bool working = false, closed = false, acknowledged = state.InstructionAcknowledged;
        // The token source must remain alive until the dialog closes, not construction.
        var operationLifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) => { closed = true; operationLifetime.Cancel(); operationLifetime.Dispose(); };
        Button apply = null!, undo = null!;
        void UpdateControls()
        {
            var selected = selection.SelectedItem as CorrectionChoice;
            newPerson.Visibility = selected?.Create == true ? Visibility.Visible : Visibility.Collapsed;
            apply.IsEnabled = !working && !string.IsNullOrWhiteSpace(sentence.EvidenceId) && selected is not null &&
                (!selected.Create || !string.IsNullOrWhiteSpace(name.Text));
            undo.IsEnabled = !working && !string.IsNullOrWhiteSpace(sentence.EvidenceId);
            selection.IsEnabled = name.IsEnabled = !working;
        }
        apply = ModernTheme.Button("Apply correction", async (_, _) =>
        {
            if (working || selection.SelectedItem is not CorrectionChoice selected) return;
            string personName = selected.Create ? name.Text.Trim() : selected.Label;
            if (selected.Special is null && !acknowledged)
            {
                if (CreateLearningDialog(dialog, personName).ShowDialog() != true)
                { UiLocalization.SetText(status, "No voice example was saved. You can cancel, choose another sentence, or use a label-only category."); return; }
            }
            working = true; UpdateControls(); UiLocalization.SetText(status, "Saving correction…");
            try
            {
                CancellationToken token = operationLifetime.Token;
                if (selected.Special is null && !acknowledged)
                { await host.AcknowledgeSpeakerLearningAsync(token); acknowledged = true; }
                string? personId = selected.PersonId;
                if (selected.Create)
                    personId = (await host.SaveSpeakerPersonAsync(personName, token: token)).Id;
                SpeakerFeedbackResult result = await host.CorrectSpeakerAsync(sentence.EvidenceId, personId, selected.Special, token);
                if (closed) return;
                if (!result.Applied) { UiLocalization.SetText(status, result.Message); return; }
                dialog.Tag = result; dialog.DialogResult = true;
            }
            catch (OperationCanceledException) { if (!closed) UiLocalization.SetText(status, "Correction cancelled."); }
            catch (Exception error) { if (!closed) UiLocalization.SetText(status, "Could not save the correction: " + error.Message); }
            finally { working = false; if (!closed) UpdateControls(); }
        }, true);
        apply.IsDefault = true; actions.Children.Add(apply);
        undo = ModernTheme.Button("Undo correction", async (_, _) =>
        {
            if (working) return;
            working = true; UpdateControls(); UiLocalization.SetText(status, "Undoing this sentence's correction…");
            try
            {
                SpeakerFeedbackResult result = await host.UndoSpeakerCorrectionAsync(sentence.EvidenceId, operationLifetime.Token);
                if (closed) return;
                if (!result.Applied) { UiLocalization.SetText(status, result.Message); return; }
                dialog.Tag = result; dialog.DialogResult = true;
            }
            catch (OperationCanceledException) { if (!closed) UiLocalization.SetText(status, "Undo cancelled."); }
            catch (Exception error) { if (!closed) UiLocalization.SetText(status, "Could not undo the correction: " + error.Message); }
            finally { working = false; if (!closed) UpdateControls(); }
        });
        actions.Children.Add(undo);
        var cancel = ModernTheme.Button("Cancel", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        body.Children.Add(actions);
        body.Children.Add(ModernTheme.Label("Undo affects only feedback attached to this sentence, when available.", 11, ModernTheme.Muted));
        if (string.IsNullOrWhiteSpace(sentence.EvidenceId)) UiLocalization.SetText(status, "This older sentence cannot be corrected. Try a newer subtitle.");
        selection.SelectionChanged += (_, _) => UpdateControls(); name.TextChanged += (_, _) => UpdateControls();
        UpdateControls(); SetBody(dialog, body);
        return dialog;
    }

    internal static Window CreateLearningDialog(Window owner, string name)
    {
        Window dialog = Shell(owner, "One clear voice example", 520);
        var body = new StackPanel();
        body.Children.Add(ModernTheme.Heading("Confirm voice example", 25));
        body.Children.Add(ModernTheme.Label("For this first example, was only this person speaking clearly?", 16));
        body.Children.Add(ModernTheme.Label("Choose a clear voice, without another person talking over them, music or a recording. A wrong example can confuse future matches. If this example is too short or unclear, the app will only update the name.", 13, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Label("When voice recognition is on, clear examples help the app recognize this person later. Music / recording and Unidentified voice never teach a voice.", 13, ModernTheme.Muted));
        var actions = new WrapPanel();
        var yes = ModernTheme.Button("Yes, this is a clear single voice", (_, _) => dialog.DialogResult = true, true);
        // Deliberately not IsDefault: opening the question or pressing Enter is not consent.
        actions.Children.Add(yes);
        var no = ModernTheme.Button("No / cancel", (_, _) => dialog.DialogResult = false); no.IsCancel = true; actions.Children.Add(no);
        body.Children.Add(actions); SetBody(dialog, body);
        dialog.Loaded += (_, _) => no.Focus();
        return dialog;
    }

    internal static Window CreatePersonDialog(Window owner, SpeakerPerson? person = null)
    {
        Window dialog = Shell(owner, person is null ? "Add a person" : "Rename person", 480);
        var body = new StackPanel();
        body.Children.Add(ModernTheme.Heading(person is null ? UiText.T("Add a person") : UiText.Format("Rename {0}", person.Name), 25));
        body.Children.Add(ModernTheme.Label("Save the name here, then correct one of their subtitles to help recognize their voice.", 13, ModernTheme.Muted));
        var name = new TextBox { Text = person?.Name ?? "", MaxLength = 64, MinHeight = 42, Margin = new Thickness(0, 6, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(name,"Person's name");
        body.Children.Add(name); var actions = new WrapPanel();
        var save = ModernTheme.Button("Save person", (_, _) => { dialog.Tag = name.Text.Trim(); dialog.DialogResult = true; }, true);
        save.IsDefault = true; save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        actions.Children.Add(save);
        var cancel = ModernTheme.Button("Cancel", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        body.Children.Add(actions); SetBody(dialog, body);
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return dialog;
    }

    internal static string DescribeResult(SpeakerFeedbackResult result) => !result.Applied ? result.Message : result.Learned
        ? result.Label + " · confirmed voice example saved. " + result.Message
        : result.Label + " · label only; no voice example added. " + result.Message;

    private static Window Shell(Window owner, string title, double width)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Icon = BrandArtwork.WindowIcon, Width = width, MaxHeight = 760, MinWidth = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ModernTheme.Background, Foreground = ModernTheme.Text,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"),
            Topmost = false, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, UseLayoutRounding = true
        };
        AppearanceTheme.ApplyWindowChrome(dialog);
        return dialog;
    }
    private static void SetBody(Window dialog, StackPanel body) {
        body.Background = AppearanceTheme.Surface;
        var layers = new Grid();
        layers.Children.Add(ThemeBackground.CreateLayer());
        layers.Children.Add(new ScrollViewer { Content = body, Padding = new Thickness(26), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dialog.Content = layers;
        UiLocalization.ApplyTree(dialog);
    }
    private sealed record CorrectionChoice(string Label, string? PersonId = null, string? Special = null, bool Create = false)
    { public override string ToString() => Label; }
}
