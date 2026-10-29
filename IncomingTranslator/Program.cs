using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IncomingTranslator;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        application.Run(new SubtitleWindow());
    }
}

internal sealed class SubtitleWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBlock _source;
    private readonly TextBlock _translation;
    private readonly CancellationTokenSource _shutdown = new();
    private IncomingTranslationService? _service;

    public SubtitleWindow()
    {
        Title = "VRChat Incoming Translation";
        Width = 960;
        Height = 245;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 14, 14, 17)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(91, 96, 110)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12, 18, 14)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock
        {
            Text = "Starting incoming translator…",
            Foreground = new SolidColorBrush(Color.FromRgb(158, 205, 255)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 28,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 56)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Close();
        header.Children.Add(_status);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        _source = new TextBlock
        {
            Text = "Listening to Windows playback…",
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 178)),
            FontSize = 17,
            Margin = new Thickness(0, 12, 0, 7),
            TextWrapping = TextWrapping.Wrap
        };
        _translation = new TextBlock
        {
            Text = "English subtitles will appear here",
            Foreground = Brushes.White,
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetRow(header, 0);
        Grid.SetRow(_source, 1);
        Grid.SetRow(_translation, 2);
        root.Children.Add(header);
        root.Children.Add(_source);
        root.Children.Add(_translation);
        border.Child = root;
        Content = border;

        MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (eventArgs.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _shutdown.Cancel();
            _service?.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 45;

        _service = new IncomingTranslationService(
            SetStatus,
            text => Dispatcher.InvokeAsync(() => _source.Text = text),
            text => Dispatcher.InvokeAsync(() => _translation.Text = text));
        try
        {
            await _service.RunAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetStatus("Stopped: " + exception.Message, true);
        }
    }

    private Task SetStatus(string text, bool error = false) =>
        Dispatcher.InvokeAsync(() =>
        {
            _status.Text = text;
            _status.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(255, 128, 128)
                : Color.FromRgb(158, 205, 255));
        }).Task;
}

internal sealed class IncomingTranslationService : IDisposable
{
    private const string VoxtralSocket = "ws://127.0.0.1:8081/v1/realtime/transcription";
    private const string TranslationEndpoint = "http://127.0.0.1:1234/v1/chat/completions";
    private const string Model = "qwen/qwen3-8b";
    private const int SettleMilliseconds = 1000;
    private const int AudioChunkBytes = 2560;
    private static readonly string LogPath = @"C:\VRChat\incoming-translator.log";

    private readonly Func<string, bool, Task> _status;
    private readonly Func<string, Task> _source;
    private readonly Func<string, Task> _translation;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly object _transcriptGate = new();
    private WasapiLoopbackCapture? _capture;
    private ClientWebSocket? _socket;
    private IWaveProvider? _pcmProvider;
    private string _latestCumulative = "";
    private string _committedBoundary = "";
    private string _activeText = "";
    private DateTimeOffset? _lastTextChange;
    private bool _settling;

    public IncomingTranslationService(
        Func<string, bool, Task> status,
        Func<string, Task> source,
        Func<string, Task> translation)
    {
        _status = status;
        _source = source;
        _translation = translation;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Log("Starting Windows WASAPI loopback capture.");

        _capture = new WasapiLoopbackCapture();
        var buffered = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _capture.DataAvailable += (_, args) => buffered.AddSamples(args.Buffer, 0, args.BytesRecorded);
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
                Log("Audio capture stopped: " + args.Exception.Message);
        };

        ISampleProvider samples = buffered.ToSampleProvider();
        ISampleProvider mono = new DownmixSampleProvider(samples);
        ISampleProvider resampled = mono.WaveFormat.SampleRate == 16000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16000);
        _pcmProvider = new SampleToWaveProvider16(resampled);

        _socket = new ClientWebSocket();
        await _status("Connecting to incoming Voxtral server…", false);
        await _socket.ConnectAsync(new Uri(VoxtralSocket), cancellationToken);
        byte[] configure = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.configure",
            transcription_delay_ms = 240,
            audio = new { format = "pcm_s16le", sample_rate = 16000, channels = 1 },
            language = "auto",
            events = new { token = false, partial = true }
        });
        await _socket.SendAsync(configure, WebSocketMessageType.Text, true, cancellationToken);
        string created = await ReceiveTextAsync(_socket, cancellationToken);
        using (JsonDocument document = JsonDocument.Parse(created))
        {
            string? type = document.RootElement.GetProperty("type").GetString();
            if (type != "session.created")
                throw new InvalidOperationException("Voxtral did not create the incoming session: " + created);
        }

        _capture.StartRecording();
        await _status("Listening privately to Windows playback • English output", false);
        Log($"Connected. Capture format: {_capture.WaveFormat}.");

        Task sender = SendAudioAsync(_socket, cancellationToken);
        Task receiver = ReceiveTranscriptsAsync(_socket, cancellationToken);
        Task settlement = SettleLoopAsync(cancellationToken);
        await Task.WhenAll(sender, receiver, settlement);
    }

    private async Task SendAudioAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] pcm = new byte[AudioChunkBytes];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            int read = _pcmProvider!.Read(pcm, 0, pcm.Length);
            if (read < pcm.Length)
                Array.Clear(pcm, read, pcm.Length - read);
            await socket.SendAsync(
                pcm.AsMemory(0, pcm.Length),
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
        }
    }

    private async Task ReceiveTranscriptsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string json = await ReceiveTextAsync(socket, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeValue)
                ? typeValue.GetString()
                : null;
            if (type is "transcript.partial" or "transcript.final")
            {
                string text = root.GetProperty("text").GetString() ?? "";
                await ObserveTranscriptAsync(text);
            }
            else if (type == "error")
            {
                throw new InvalidOperationException("Voxtral error: " + json);
            }
            else if (type == "session.warning")
            {
                Log("Voxtral warning: " + json);
            }
        }
    }

    private async Task ObserveTranscriptAsync(string cumulative)
    {
        string active;
        lock (_transcriptGate)
        {
            if (string.Equals(cumulative, _latestCumulative, StringComparison.Ordinal))
                return;
            _latestCumulative = cumulative;
            if (!_latestCumulative.StartsWith(_committedBoundary, StringComparison.Ordinal))
                _committedBoundary = "";
            _activeText = _latestCumulative[_committedBoundary.Length..].TrimStart();
            _lastTextChange = DateTimeOffset.UtcNow;
            active = _activeText;
        }
        if (!string.IsNullOrWhiteSpace(active))
            await _source("Heard: " + active);
    }

    private async Task SettleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            string? settled = null;
            lock (_transcriptGate)
            {
                if (!_settling &&
                    !string.IsNullOrWhiteSpace(_activeText) &&
                    _lastTextChange is not null &&
                    DateTimeOffset.UtcNow - _lastTextChange.Value >= TimeSpan.FromMilliseconds(SettleMilliseconds))
                {
                    settled = _activeText;
                    _committedBoundary = _latestCumulative;
                    _activeText = "";
                    _lastTextChange = null;
                    _settling = true;
                }
            }
            if (settled is not null)
                _ = TranslateAndPublishAsync(settled, cancellationToken);
        }
    }

    private async Task TranslateAndPublishAsync(string sourceText, CancellationToken cancellationToken)
    {
        await _translationGate.WaitAsync(cancellationToken);
        try
        {
            await _status("Translating incoming speech…", false);
            var payload = new
            {
                model = Model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "Translate the source speech into concise natural English. Preserve meaning and tone. Do not explain or add anything. Reply only with the English translation. /no_think"
                    },
                    new { role = "user", content = sourceText }
                },
                temperature = 0.2,
                max_tokens = 256
            };
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                TranslationEndpoint,
                payload,
                cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Bionic returned {(int)response.StatusCode}: {body}");
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");
            string translated = content.GetString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("Bionic returned an empty translation.");
            Log($"Source: {sourceText} | English: {translated}");
            await _translation(translated);
            await _status("Listening privately to Windows playback • English output", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log("Translation failed: " + exception);
            await _status("Translation error: " + exception.Message, true);
        }
        finally
        {
            lock (_transcriptGate) _settling = false;
            _translationGate.Release();
        }
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The incoming Voxtral WebSocket closed unexpectedly.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Voxtral sent an unexpected binary response.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static void Log(string message)
    {
        File.AppendAllText(
            LogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _socket?.Dispose();
        _http.Dispose();
        _translationGate.Dispose();
    }
}

internal sealed class DownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _sourceBuffer = Array.Empty<float>();

    public DownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = _source.WaveFormat.Channels;
        int required = count * channels;
        if (_sourceBuffer.Length < required)
            _sourceBuffer = new float[required];
        int sourceRead = _source.Read(_sourceBuffer, 0, required);
        int frames = sourceRead / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            int frameOffset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                sum += _sourceBuffer[frameOffset + channel];
            buffer[offset + frame] = sum / channels;
        }
        return frames;
    }
}
