using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Generated pictures, temporary names and inert services only. No audio, native
// recognition model, network, live preferences or user photos are accessed.
internal static class PersonAvatarTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("People profiles: " + detail); checks++; }
        void Reject<T>(Action action, string detail) where T : Exception
        {
            bool failed = false; try { action(); } catch (T) { failed = true; }
            Check(failed, detail);
        }
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string Data(string name) => Path.Combine(directory, name);
        string feedback = Data("people.json"), legacy = Data("v1.json");
        const string oldJson = "{\"Version\":1,\"People\":[{\"Id\":\"old\",\"Name\":\"Existing friend\",\"LastUsedAtUtc\":\"2026-01-01T00:00:00Z\"}],\"Anchors\":[],\"InstructionAcknowledged\":true}";
        File.WriteAllText(legacy, oldJson);
        var oldStore = new SpeakerFeedbackStore(legacy);
        SpeakerPerson old = oldStore.State().People.Single();
        Check(old.TranslationCount == 0 && old.AvatarFileName is null && old.CurrentModelConfirmedSamples is null && old.LastUsedAtUtc.Year == 2026,
            "old V1 people retain recency and do not invent statistics or pictures");
        Check(File.ReadAllText(legacy) == oldJson, "browsing old names does not rewrite them");
        var store = new SpeakerFeedbackStore(feedback);
        SpeakerPerson alice = store.SavePerson("Alice Example", null), bob = store.SavePerson("Bob", null);
        string session = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        store.RecordTranslation(session + ":1", alice.Id, now);
        store.RecordTranslation(session + ":1", alice.Id, now.AddSeconds(1));
        Check(store.RequirePerson(alice.Id).TranslationCount == 1 && store.RequirePerson(alice.Id).LastTranslatedAtUtc == now,
            "repeated revisions of a successful final sentence count once");
        store.RecordTranslation(session + ":1", bob.Id, now.AddSeconds(1));
        Check(store.RequirePerson(alice.Id).TranslationCount == 0 && store.RequirePerson(bob.Id).TranslationCount == 1,
            "changing the person moves rather than copies the translation count");
        store.RecordTranslation(session + ":1", null, now);
        Check(store.RequirePerson(bob.Id).TranslationCount == 0, "music or unknown corrections remove the person's count");
        store.RecordTranslation(session + ":1", alice.Id, now);
        store.RecordTranslation(session + ":2", alice.Id, now.AddSeconds(-1));
        Check(store.RequirePerson(alice.Id).TranslationCount == 2 && store.RequirePerson(alice.Id).LastTranslatedAtUtc == now,
            "different sentences count and backwards timestamps do not demote recent activity");
        Check(store.RequirePerson(alice.Id).LastUsedAtUtc == default, "automatic translations never alter manual-picker recency");
        Check(new SpeakerFeedbackStore(feedback).RequirePerson(alice.Id).TranslationCount == 2, "counts survive reload");
        string before = File.ReadAllText(feedback);
        store.RecordTranslation(session + ":7", null, now);
        Check(File.ReadAllText(feedback) == before, "new unnamed subtitles do not cause unnecessary voice-database writes");
        Reject<ArgumentException>(() => store.RecordTranslation("raw transcript forbidden", alice.Id, now), "count identity rejects text content");
        Reject<ArgumentException>(() => store.RecordTranslation(session + ":0", alice.Id, now), "invalid count identity rejected");
        Reject<ArgumentException>(() => store.RecordTranslation(session + ":3", "missing", now), "missing person cannot acquire translations");
        Check(File.ReadAllText(feedback) == before, "invalid metadata cannot alter the saved database");
        using (var held = new FileStream(feedback, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject<IOException>(() => store.RecordTranslation(session + ":3", alice.Id, now), "failed count write is reported");
        Check(store.RequirePerson(alice.Id).TranslationCount == 2 && File.ReadAllText(feedback) == before,
            "failed count writes retain persisted and visible state");

        string source = Data("original.png"); CreatePicture(source);
        byte[] original = File.ReadAllBytes(source);
        SpeakerPerson photo = store.SetPersonPhoto(alice.Id, source);
        Check(PersonAvatar.IsManagedFileName(photo.AvatarFileName) && !photo.AvatarFileName!.Contains("original", StringComparison.Ordinal) &&
            !File.ReadAllText(feedback).Contains(source, StringComparison.Ordinal), "only a generated local PNG leaf is saved");
        BitmapSource? loaded = PersonAvatar.Load(photo, directory);
        Check(loaded is { PixelWidth: 512, PixelHeight: 512, IsFrozen: true }, "import creates a frozen full-size square profile photo");
        BitmapSource? tiny = PersonAvatar.Load(photo, directory, thumbnail: true);
        Check(tiny is { PixelWidth: 64, PixelHeight: 64, IsFrozen: true } && !ReferenceEquals(loaded, tiny),
            "subtitle thumbnails are genuinely smaller than the full profile image");
        var fullView = (Grid)PersonAvatar.Create(photo, 96, Brushes.Gray, Brushes.White, dataRoot: directory);
        var subtitleView = (Grid)PersonAvatar.Create(photo, 30, Brushes.Gray, Brushes.White, dataRoot: directory);
        Check(fullView.Width == 96 && fullView.Children.OfType<Image>().Single().Source is BitmapSource { PixelWidth: 512 } &&
            subtitleView.Width == 30 && subtitleView.Children.OfType<Image>().Single().Source is BitmapSource { PixelWidth: 64 },
            "large profile cards use full photos while subtitle badges use compact decoded images");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "import never changes the original user picture");
        Check(ReferenceEquals(loaded, PersonAvatar.Load(photo, directory)), "thumbnail reads use a bounded cache");
        string photoPath = Path.Combine(PersonAvatar.PhotoDirectory(directory), photo.AvatarFileName!);
        using (var exclusive = new FileStream(photoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusive.Length > 0, "displaying a thumbnail leaves no file lock");
        string savedPhoto = photo.AvatarFileName!;
        string photoBefore = File.ReadAllText(feedback);
        int filesBefore = Directory.EnumerateFiles(PersonAvatar.PhotoDirectory(directory)).Count();
        using (var held = new FileStream(feedback, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject<IOException>(() => store.SetPersonPhoto(alice.Id, source), "failed picture metadata write is reported");
        Check(store.RequirePerson(alice.Id).AvatarFileName == savedPhoto && File.ReadAllText(feedback) == photoBefore &&
            Directory.EnumerateFiles(PersonAvatar.PhotoDirectory(directory)).Count() == filesBefore,
            "failed photo write retains the old picture and removes only the new uncommitted thumbnail");
        Reject<ArgumentException>(() => store.SetPersonPhoto(alice.Id, "https://example.com/photo.png"), "remote image URLs are not imported");
        Reject<ArgumentException>(() => store.SetPersonPhoto(alice.Id, "..\\photo.png"), "relative input paths are not imported");
        Reject<ArgumentException>(() => store.SetPersonPhoto(alice.Id, "\\\\server\\photo.png"), "network-share input is not imported");
        File.WriteAllText(Data("not-picture.png"), "synthetic corrupt image");
        bool corruptRejected = false;
        try { store.SetPersonPhoto(alice.Id, Data("not-picture.png")); } catch { corruptRejected = true; }
        Check(corruptRejected && store.RequirePerson(alice.Id).AvatarFileName == savedPhoto, "invalid source leaves the current picture intact");
        File.WriteAllBytes(Data("oversized.png"), new byte[10 * 1024 * 1024 + 1]);
        Reject<PersonPhotoException>(() => store.SetPersonPhoto(alice.Id, Data("oversized.png")), "oversized compressed input rejected before decoding");
        string outside = Data("keep.txt"); File.WriteAllText(outside, "keep");
        PersonAvatar.DeleteManaged("..\\keep.txt", directory);
        Check(File.ReadAllText(outside) == "keep" && PersonAvatar.Load(photo with { AvatarFileName = "..\\keep.txt" }, directory) is null,
            "managed picture operations reject traversal without reading or deleting arbitrary files");
        Check(!PersonAvatar.IsManagedFileName(new string('A', 32) + ".png"), "managed pictures use one canonical lowercase filename format");
        string linkedRoot = Data("linked-photo-parent"); bool linkCreated = false;
        try { Directory.CreateSymbolicLink(linkedRoot, directory); linkCreated = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linkCreated)
        {
            try
            {
                Reject<IOException>(() => PersonAvatar.Import(Path.Combine(linkedRoot, "original.png"), directory),
                    "a source file behind a linked parent is rejected");
                Reject<IOException>(() => PersonAvatar.Import(source, Path.Combine(linkedRoot, "must-not-create")),
                    "storage linked parents are rejected before creating missing child folders");
                Check(!Directory.Exists(Data("must-not-create")), "linked storage cannot create folders in its redirected target");
                PersonAvatar.DeleteManaged(photo.AvatarFileName, linkedRoot);
                Check(File.Exists(photoPath) && PersonAvatar.Load(photo, linkedRoot) is null,
                    "linked storage cannot delete or display redirected thumbnails");
            }
            finally { Directory.Delete(linkedRoot); }
        }
        File.WriteAllText(photoPath, "corrupt managed photo");
        Check(PersonAvatar.Load(photo, directory) is null, "corrupt saved thumbnails degrade to initials");
        Check(PersonAvatar.Initials("Alice Example") == "AE" && PersonAvatar.Initials("李 小龙") == "李小" && PersonAvatar.Initials("") == "?",
            "initials work without photos across languages");
        FrameworkElement fallback = PersonAvatar.Create(photo, 40, Brushes.Gray, Brushes.White, dataRoot: directory);
        Check(fallback is Grid grid && grid.Children.OfType<Image>().Single().Source is DrawingImage,
            "corrupt picture renders deterministic local artwork");
        photo = store.SetPersonPhoto(alice.Id, source);
        Check(!File.Exists(photoPath), "successful replacement removes the old managed picture");
        photoPath = Path.Combine(PersonAvatar.PhotoDirectory(directory), photo.AvatarFileName!);
        store.SetPersonPhoto(alice.Id, null);
        Check(store.RequirePerson(alice.Id).AvatarFileName is null && !File.Exists(photoPath), "removing a picture preserves the person and removes its thumbnail");
        photo = store.SetPersonPhoto(bob.Id, source);
        store.DeletePerson(bob.Id);
        Check(!File.Exists(Path.Combine(PersonAvatar.PhotoDirectory(directory), photo.AvatarFileName!)) && new SpeakerFeedbackStore(feedback).State().People.Count == 1,
            "deleting a person persists and removes only their managed picture");

        string modelA = new('A', 64), modelB = new('B', 64);
        store.ReplaceContribution("voice-a", new SpeakerFeedbackAnchor { EvidenceId = "voice-a", PersonId = alice.Id, ModelSha = modelA,
            Embedding = [1, 0], SpeechSeconds = 2, ClippedFraction = 0, RevisionToken = Guid.NewGuid().ToString("N") });
        store.ReplaceContribution("voice-b", new SpeakerFeedbackAnchor { EvidenceId = "voice-b", PersonId = alice.Id, ModelSha = modelB,
            Embedding = [1, 0], SpeechSeconds = 2, ClippedFraction = 0, RevisionToken = Guid.NewGuid().ToString("N") });
        Check(store.State().People.Single().ConfirmedSamples == 2 && store.State().People.Single().CurrentModelConfirmedSamples is null &&
            store.State(modelA).People.Single().CurrentModelConfirmedSamples == 1,
            "all saved examples are distinct from active-model examples, not an accuracy percentage");
        store.SavePerson("Alice renamed", alice.Id);
        Check(store.RequirePerson(alice.Id).TranslationCount == 2 && store.RequirePerson(alice.Id).ConfirmedSamples == 2,
            "rename preserves counters and examples");
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(feedback)))
            Check(document.RootElement.GetProperty("Version").GetInt32() == 1 && !File.ReadAllText(feedback).Contains("synthetic source", StringComparison.Ordinal),
                "backwards-compatible saved counters contain no transcript content");
        string boundedPath = Data("bounded-counts.json"), boundedPerson = "bounded-person";
        File.WriteAllText(boundedPath, JsonSerializer.Serialize(new { Version = 1, InstructionAcknowledged = false,
            People = new[] { new { Id = boundedPerson, Name = "Long-term friend", TranslationCount = 4096L } },
            Anchors = Array.Empty<SpeakerFeedbackAnchor>(), RecentTranslations = Enumerable.Range(1, 4096)
                .Select(index => new { Id = session + ":" + index, PersonId = boundedPerson, AtUtc = now.AddSeconds(index) }).ToArray() }));
        var bounded = new SpeakerFeedbackStore(boundedPath);
        bounded.RecordTranslation(session + ":4097", boundedPerson, now.AddSeconds(4097));
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(boundedPath)))
            Check(document.RootElement.GetProperty("RecentTranslations").GetArrayLength() == 4096 &&
                bounded.RequirePerson(boundedPerson).TranslationCount == 4097 && new SpeakerFeedbackStore(boundedPath).RequirePerson(boundedPerson).TranslationCount == 4097,
                "recent deduplication remains bounded without erasing the lifetime total");
        checks += CheckOverlayAvatar(directory, source);
        checks += Task.Run(() => CheckInertService(Path.Combine(directory, "inert-service"))).GetAwaiter().GetResult();
        return checks;
    }

    private static int CheckInertService(string directory)
    {
        Directory.CreateDirectory(directory); int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("People profile service: " + detail); checks++; }
        using var identifier = new AiSpeakerIdentifier(Path.Combine(directory, "anonymous.json"), Path.Combine(directory, "people.json"));
        identifier.SetRecognitionEnabledAsync(false).GetAwaiter().GetResult();
        using var service = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, identifier);
        SpeakerPerson alice = service.SaveSpeakerPersonAsync("Alice").GetAwaiter().GetResult();
        SpeakerPerson bob = service.SaveSpeakerPersonAsync("Bob").GetAwaiter().GetResult();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SpeakerAttribution observed = identifier.RecordLabelOnlyObservation(new SpeakerAttribution("Alice", 0, false, 0) { PersonId = alice.Id });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo create = typeof(IncomingTranslationService).GetMethod("CreateTranslationRequest", flags)!;
        MethodInfo record = typeof(IncomingTranslationService).GetMethod("RecordPublishedTranslationAsync", flags)!;
        TranslationRequest request = (TranslationRequest)create.Invoke(service, ["synthetic source", observed, now, now, 1L, null])!;
        service.CorrectSpeakerAsync(observed.EvidenceId, alice.Id).GetAwaiter().GetResult();
        void Record(TranslationRequest item) => ((Task)record.Invoke(service, [item, CancellationToken.None])!).GetAwaiter().GetResult();
        Record(request with { IsCommitted = false });
        Check(service.SpeakerFeedbackSnapshot!.People.All(person => person.TranslationCount == 0), "preview translations never increment totals");
        Record(request); Record(request with { At = now.AddSeconds(1) });
        Check(service.SpeakerFeedbackSnapshot!.People.Single(person => person.Id == alice.Id).TranslationCount == 1,
            "successful final segment and revisions increment once");
        SpeakerFeedbackResult corrected = service.CorrectSpeakerAsync(observed.EvidenceId, bob.Id).GetAwaiter().GetResult();
        Check(corrected.Applied && !corrected.Learned && service.SpeakerFeedbackSnapshot!.People.Single(person => person.Id == alice.Id).TranslationCount == 0 &&
            service.SpeakerFeedbackSnapshot.People.Single(person => person.Id == bob.Id).TranslationCount == 1,
            "live label-only correction moves the already displayed final count");
        service.UndoSpeakerCorrectionAsync(observed.EvidenceId).GetAwaiter().GetResult();
        Check(service.SpeakerFeedbackSnapshot!.People.Single(person => person.Id == alice.Id).TranslationCount == 1 &&
            service.SpeakerFeedbackSnapshot.People.Single(person => person.Id == bob.Id).TranslationCount == 0,
            "undo restores the count as well as the displayed assignment");
        SpeakerAttribution unknown = identifier.RecordLabelOnlyObservation(new SpeakerAttribution("Voice", 0, false, 0));
        TranslationRequest unnamed = (TranslationRequest)create.Invoke(service, ["synthetic unknown", unknown, now, now, 2L, null])!;
        string beforeUnknown = File.ReadAllText(Path.Combine(directory, "people.json"));
        Record(unnamed);
        Check(File.ReadAllText(Path.Combine(directory, "people.json")) == beforeUnknown,
            "successful unnamed translations retain live metadata without a disk transaction");
        service.CorrectSpeakerAsync(unknown.EvidenceId, bob.Id).GetAwaiter().GetResult();
        Check(service.SpeakerFeedbackSnapshot!.People.Single(person => person.Id == bob.Id).TranslationCount == 1,
            "an unnamed final can still be counted when the user later names it");
        Check(!identifier.IsModelLoaded && identifier.ExtractorLoadAttempts == 0 &&
            !File.Exists(Path.Combine(directory, "anonymous.json")), "profile statistics and corrections never load a model or record audio");
        return checks;
    }

    private static int CheckOverlayAvatar(string directory, string source)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("People subtitle pictures: " + detail); checks++; }
        Check(string.Equals(Directory.GetParent(directory)?.FullName, Directory.GetParent(AppPaths.Root)?.FullName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable) ?? ""), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase),
            "overlay picture tests require the isolated desktop data root");
        string photo = PersonAvatar.Import(source, AppPaths.Root);
        try
        {
            var alice = new SpeakerPerson("overlay-alice", "Alice Example", 2) { AvatarFileName = photo };
            var bob = new SpeakerPerson("overlay-bob", "Bob Friend", 0);
            var overlay = new SubtitleWindow();
            var overlaySettings = (AppSettings)typeof(SubtitleWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
            overlaySettings.OverlayMode = "Sentence bubbles"; overlaySettings.HistoryLinesPerSpeaker = 10;
            overlay.RefreshSpeakerNames(new([alice, bob], false));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            overlay.Publish(new("Alice Example", "Synthetic source", "Hello!", 91, now) { PersonId = alice.Id });
            overlay.Publish(new("Bob Friend", "Synthetic source", "How are you?", 92, now.AddSeconds(1)) { PersonId = bob.Id });
            var content = (FrameworkElement)overlay.Content;
            content.Measure(new Size(680, 480)); content.Arrange(new Rect(0, 0, 680, 480)); content.UpdateLayout();
            static IEnumerable<DependencyObject> Nodes(DependencyObject node)
            {
                yield return node;
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                    foreach (DependencyObject child in Nodes(VisualTreeHelper.GetChild(node, index))) yield return child;
            }
            Check(Nodes(content).OfType<Image>().Any(image => image.Source is BitmapSource bitmap && bitmap.PixelWidth == 64),
                "named subtitle bubbles show a small local profile thumbnail");
            Check(Nodes(content).OfType<Image>().Any(image => ReferenceEquals(image.Source, GeneratedPersonAvatar.Resolve(bob))),
                "a named person without a photo has generated artwork beside their translation");
            var preview = new RenderTargetBitmap(680, 480, 96, 96, PixelFormats.Pbgra32); preview.Render(content);
            using (var output = new FileStream(Path.Combine(directory, "people-subtitle-bubbles.png"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview)); encoder.Save(output);
            }
            overlay.RefreshSpeakerNames(new([alice with { AvatarFileName = null, Name = "Alice Renamed" }, bob], false));
            content.UpdateLayout();
            Check(overlay.HistoryEntry(91)?.Speaker == "Alice Renamed" &&
                Nodes(content).OfType<Image>().Any(image => ReferenceEquals(image.Source, GeneratedPersonAvatar.Resolve(alice))),
                "photo removal and rename update existing bubbles with stable artwork without restarting a service");
            overlay.RefreshSpeakerNames(new([bob], false));
            Check(overlay.HistoryEntry(91)?.PersonId is null && overlay.HistoryEntryCount == 2,
                "deleting a person clears their badge while keeping subtitle history");
            overlay.Close();
        }
        finally { PersonAvatar.DeleteManaged(photo, AppPaths.Root); }
        return checks;
    }

    private static void CreatePicture(string path)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Coral, null, new Rect(0, 0, 512, 256));
            context.DrawEllipse(Brushes.White, null, new Point(256, 128), 70, 70);
        }
        var image = new RenderTargetBitmap(512, 256, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output);
    }
}
