using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

// All values describe the CURRENT bootstrap phase, never overall setup completion.
// DownloadTotalBytes is apt's announced download requirement, not received bytes.
// Get: records a file starting; only top-level Ninja output supplies completed units.
// Fraction means APT's weighted current transaction work or Ninja task progress,
// never byte progress or a percentage of the complete app setup.
internal sealed record SpeechSetupProgressSnapshot(string Stage, string Message, int StepIndex, int StepCount,
    double? Fraction, int CompletedUnits, int TotalUnits, long? DownloadTotalBytes,
    TimeSpan StageElapsed, TimeSpan LastActivityAge)
{
    public int? DownloadFileNumber { get; init; }
    public bool ServerMarkerSeen { get; init; }
    public bool ScriptReportedFinished { get; init; }
}

// Pure, bounded, thread-safe interpretation of stdout/stderr. The caller supplies
// timestamps, persists diagnostics and decides whether the helper really succeeded.
internal sealed class SpeechSetupProgressParser
{
    private static readonly Regex Step = Pattern(@"^Step\s+(?<step>[1-5])/5:\s*(?<description>.*)$");
    private static readonly Regex AptRequirement = Pattern(@"^Need to get\s+(?<amount>[\d.,]+)\s*(?<unit>[kMGT]?B)(?:/[\d.,]+\s*[kMGT]?B)?\s+of archives\b");
    private static readonly Regex AptFile = Pattern(@"^Get:(?<file>[0-9]+)\s+");
    private static readonly Regex Ninja = Pattern(@"^\[(?<done>[0-9]+)/(?<total>[0-9]+)\]\s+(?<work>.+)$");
    private static readonly Regex BuildDirectory = Pattern(@"[/\\]build-(?:cuda|vulkan|cpu)[/\\]?\s*$");
    private static readonly Regex Ansi = Pattern(@"\x1B\[[0-?]*[ -/]*[@-~]");
    private readonly object _gate = new();
    private DateTimeOffset _stageStartedAt;
    private DateTimeOffset _lastActivityAt;
    private string _stage = "Preparing speech recognition";
    private string _message = "Checking the speech tools";
    private int _stepIndex;
    private bool _gpuTools;
    private bool _packageDownload;
    private bool _topLevelBuildReady;
    private long? _downloadTotalBytes;
    private int? _downloadFileNumber;
    private int _completedUnits;
    private int _totalUnits;
    private double? _workFraction;
    private bool _installationProgress;
    private bool _serverMarkerSeen;
    private bool _scriptReportedFinished;

    public SpeechSetupProgressParser(DateTimeOffset startedAt)
    { _stageStartedAt = _lastActivityAt = startedAt; }

    public SpeechSetupProgressSnapshot Observe(string line, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            string text = Clean(line);
            if (text.Length != 0)
            {
                // Concurrent stdout/stderr can arrive out of timestamp order.
                if (timestamp > _lastActivityAt) _lastActivityAt = timestamp;
                Interpret(text, _lastActivityAt);
            }
            return Snapshot(timestamp);
        }
    }

    // Heartbeats use this without inventing activity or advancing progress.
    public SpeechSetupProgressSnapshot GetSnapshot(DateTimeOffset now)
    { lock (_gate) return Snapshot(now); }

    private void Interpret(string text, DateTimeOffset timestamp)
    {
        Match phase = Step.Match(text);
        if (phase.Success)
        {
            int next = int.Parse(phase.Groups["step"].Value, CultureInfo.InvariantCulture);
            if (next < _stepIndex) return; // Late stderr must not rewind the phase.
            if (next != _stepIndex)
            {
                _stepIndex = next;
                _stageStartedAt = timestamp;
                _downloadTotalBytes = null;
                _downloadFileNumber = null;
                _packageDownload = false;
                _topLevelBuildReady = false;
                _completedUnits = _totalUnits = 0;
                _workFraction = null;
                _installationProgress = false;
            }
            string description = phase.Groups["description"].Value;
            _gpuTools = next == 2 && (description.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("Vulkan", StringComparison.OrdinalIgnoreCase));
            (_stage, _message) = next switch
            {
                1 => ("Preparing speech tools", "Checking and installing the required build tools"),
                2 when _gpuTools => ("Preparing GPU support", "Checking and installing the GPU build tools"),
                2 => ("Preparing speech support", "Preparing speech recognition for the selected processor"),
                3 => ("Preparing speech files", "Downloading and checking the speech engine files"),
                4 => ("Building speech recognition", "Configuring the speech engine"),
                _ => ("Checking speech recognition", "Checking the finished speech engine")
            };
            return;
        }

        if (_stepIndex is 1 or 2)
        {
            string tools = _gpuTools ? "GPU build tools" : "build tools";
            // Status-Fd lines have three structural colons; the remaining
            // human description can itself contain colons and is never shown.
            if (text.StartsWith("dlstatus:", StringComparison.Ordinal) ||
                text.StartsWith("pmstatus:", StringComparison.Ordinal))
            {
                string[] fields = text.Split(':', 4);
                if (fields.Length == 4 && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double percent) && double.IsFinite(percent) && percent is >= 0 and <= 100)
                {
                    _completedUnits = _totalUnits = 0;
                    _workFraction = percent / 100;
                    bool downloading = fields[0] == "dlstatus";
                    _installationProgress = !downloading;
                    _packageDownload = downloading;
                    if (downloading && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture,
                            out int number) && number > 0)
                        _downloadFileNumber = Math.Max(number, _downloadFileNumber ?? 0);
                    string verb = downloading ? "Downloading" : "Installing";
                    _message = $"{verb} {tools} · {percent.ToString("0", CultureInfo.InvariantCulture)}%";
                }
                return;
            }
            Match required = AptRequirement.Match(text);
            if (required.Success && ParseBytes(required.Groups["amount"].Value, required.Groups["unit"].Value) is long bytes)
            {
                _downloadTotalBytes = bytes;
                _downloadFileNumber = null;
                _packageDownload = bytes > 0;
                _workFraction = null;
                _installationProgress = false;
                _message = bytes > 0 ? $"Downloading {tools} · about {FormatBytes(bytes)} to download" :
                    $"The {tools} are already downloaded; installing them";
                return;
            }
            Match file = AptFile.Match(text);
            if (file.Success)
            {
                if (_packageDownload && int.TryParse(file.Groups["file"].Value, out int number) && number > 0)
                {
                    _downloadFileNumber = Math.Max(number, _downloadFileNumber ?? 0);
                    _message = $"Downloading {tools} · fetching file {_downloadFileNumber}" +
                        (_downloadTotalBytes is long total ? $" · about {FormatBytes(total)} in total" : "");
                }
                else if (!_packageDownload) _message = $"Checking available {tools}";
                return;
            }
            if (text.StartsWith("Fetched ", StringComparison.Ordinal))
            {
                _packageDownload = false;
                _workFraction = null;
                _installationProgress = false;
                _message = $"Downloads finished; installing {tools}";
                return;
            }
            if (text.StartsWith("Unpacking ", StringComparison.Ordinal) ||
                text.StartsWith("Preparing to unpack ", StringComparison.Ordinal))
            {
                _packageDownload = false;
                if (!_installationProgress) _workFraction = null;
                _message = $"Installing {tools}";
                return;
            }
            if (text.StartsWith("Setting up ", StringComparison.Ordinal) ||
                text.StartsWith("Processing triggers for ", StringComparison.Ordinal))
            {
                _packageDownload = false;
                if (!_installationProgress) _workFraction = null;
                _message = $"Finishing installation of {tools}";
                return;
            }
        }

        if (_stepIndex == 4)
        {
            // CMake uses a separate [n/m] Ninja build to fetch dependencies. Its
            // 100% is NOT the speech build. Wait for the top-level build directory.
            const string configured = "-- Build files have been written to:";
            if (text.StartsWith(configured, StringComparison.Ordinal) && BuildDirectory.IsMatch(text[configured.Length..].Trim()))
            {
                _topLevelBuildReady = true;
                _message = "Compiling the speech engine";
                return;
            }
            Match task = Ninja.Match(text);
            if (_topLevelBuildReady && task.Success &&
                int.TryParse(task.Groups["done"].Value, out int done) &&
                int.TryParse(task.Groups["total"].Value, out int total) && total > 0 && done <= total &&
                (total != _totalUnits || done >= _completedUnits))
            {
                _completedUnits = done;
                _totalUnits = total;
                _workFraction = done / (double)total;
                string verb = task.Groups["work"].Value.StartsWith("Linking ", StringComparison.Ordinal) ? "Finishing" : "Compiling";
                _message = $"{verb} the speech engine · {done} of {total} build tasks";
                return;
            }
            if (text.StartsWith("ninja: no work to do.", StringComparison.Ordinal))
                _message = "The speech engine is already built";
        }

        if (_stepIndex == 5 && text.StartsWith("LOCALTALK_VOXTRAL_SERVER=/", StringComparison.Ordinal))
        {
            _serverMarkerSeen = true;
            _message = "Speech engine checked; finishing setup";
        }
        if (_stepIndex == 5 && text.StartsWith("Speech runtime setup finished.", StringComparison.Ordinal))
        {
            _scriptReportedFinished = true;
            _message = "Speech tools prepared; finishing setup";
        }
    }

    private SpeechSetupProgressSnapshot Snapshot(DateTimeOffset now) => new(_stage, _message, _stepIndex, 5,
        _workFraction,
        _completedUnits, _totalUnits, _downloadTotalBytes,
        NonNegative(now - _stageStartedAt), NonNegative(now - _lastActivityAt))
    {
        DownloadFileNumber = _downloadFileNumber,
        ServerMarkerSeen = _serverMarkerSeen,
        ScriptReportedFinished = _scriptReportedFinished
    };

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string Clean(string? line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        string bounded = line.Length > 4096 ? line[..4096] : line;
        bounded = Ansi.Replace(bounded, "");
        return new string(bounded.Where(character => !char.IsControl(character) || character == '\t').ToArray()).Trim();
    }
    private static long? ParseBytes(string amount, string unit)
    {
        if (!double.TryParse(amount.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0) return null;
        double multiplier = unit switch { "B" => 1, "kB" => 1e3, "MB" => 1e6, "GB" => 1e9, "TB" => 1e12, _ => 0 };
        double bytes = value * multiplier;
        return multiplier > 0 && bytes < long.MaxValue ? (long)Math.Round(bytes, MidpointRounding.AwayFromZero) : null;
    }
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (bytes / 1e3).ToString("0", CultureInfo.InvariantCulture) + " kB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };
}
