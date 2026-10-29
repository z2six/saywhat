using System;

namespace IncomingTranslator;

// No filesystem, WSL, process, network, audio or models: captured/synthetic text only.
internal static class SpeechSetupProgressTests
{
    public static int RunOfflineSelfTests()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Speech setup progress test failed: " + message);
            checks++;
        }
        DateTimeOffset start = new(2026, 10, 5, 19, 30, 0, TimeSpan.Zero);
        var parser = new SpeechSetupProgressParser(start);
        SpeechSetupProgressSnapshot Read(string line, int second) => parser.Observe(line, start.AddSeconds(second));
        SpeechSetupProgressSnapshot initial = parser.GetSnapshot(start);
        Check(initial.StepIndex == 0 && initial.StepCount == 5 && initial.Fraction is null, "initialization does not invent work or completion");
        Check(initial.StageElapsed == TimeSpan.Zero && initial.LastActivityAge == TimeSpan.Zero, "initial timing starts at the supplied time");
        Check(Read("", 10).LastActivityAge == TimeSpan.FromSeconds(10), "empty output is not activity");
        Check(Read("  \0 \r\n", 11).LastActivityAge == TimeSpan.FromSeconds(11), "blank/control-only output is not activity");
        Check(Read("Reading package lists...", 12).LastActivityAge == TimeSpan.Zero, "unknown real output records activity");
        SpeechSetupProgressSnapshot tools = Read("Step 1/5: Installing signed Ubuntu build packages (may download several hundred MB).", 20);
        Check(tools.StepIndex == 1 && tools.Stage == "Preparing speech tools", "Ubuntu step becomes a friendly tool phase");
        Check(tools.StageElapsed == TimeSpan.Zero && tools.Fraction is null, "new stage timing resets without a fake percentage");
        Check(Read("Get:1 http://archive.ubuntu.com noble InRelease [256 kB]", 21).DownloadFileNumber is null,
            "apt repository metadata is not counted as a package download");
        SpeechSetupProgressSnapshot requirement = Read("Need to get 165 MB of archives.", 25);
        Check(requirement.DownloadTotalBytes == 165_000_000 && requirement.Fraction is null, "announced package size is known but received fraction is not");
        Check(requirement.Message.Contains("165.0 MB", StringComparison.Ordinal), "required size is shown without raw package names");
        SpeechSetupProgressSnapshot file = Read("Get:1 http://archive.ubuntu.com gcc-13 amd64 13.3.0 [54.1 MB]", 26);
        Check(file.DownloadFileNumber == 1 && file.Message.Contains("fetching file 1", StringComparison.Ordinal), "a file start updates a useful download detail");
        Check(file.CompletedUnits == 0 && file.TotalUnits == 0 && file.Fraction is null, "a file start is never a completed byte or completed task");
        Check(Read("Get:2 https://archive.ubuntu.com cmake amd64 3.28 [11.4 MB]", 27).DownloadFileNumber == 2, "successive files advance the current file number");
        Check(Read("Get:1 https://archive.ubuntu.com gcc-13 retry [54.1 MB]", 28).DownloadFileNumber == 2, "late/retried files do not rewind file detail");
        Check(Read("Get:999999999999999999 https://example.invalid huge", 29).DownloadFileNumber == 2, "overflowing file identifiers are ignored safely");
        Check(Read("Fetched 165 MB in 15s (11 MB/s)", 30).Message == "Downloads finished; installing build tools", "finished downloading transitions to installation");
        Check(Read("Unpacking gcc-13 (13.3.0-6ubuntu2) ...", 31).Message == "Installing build tools", "unpacking is reported as installation");
        Check(Read("Setting up cmake (3.28.3-1build7) ...", 32).Message == "Finishing installation of build tools", "package configuration has an understandable detail");
        Check(Read("Processing triggers for man-db (2.12.0-4build2) ...", 33).Message == "Finishing installation of build tools", "package triggers retain the installation phase");
        Check(parser.GetSnapshot(start.AddSeconds(43)).StageElapsed == TimeSpan.FromSeconds(23), "stage elapsed includes download and install time");
        Check(parser.GetSnapshot(start.AddSeconds(43)).LastActivityAge == TimeSpan.FromSeconds(10), "idle age is measured separately from elapsed stage time");

        SpeechSetupProgressSnapshot gpu = Read("Step 2/5: Installing the CUDA 13.1 TOOLKIT only (multi-GB download). No Linux display driver is installed.", 50);
        Check(gpu.Stage == "Preparing GPU support" && gpu.StepIndex == 2 && gpu.DownloadTotalBytes is null && gpu.DownloadFileNumber is null,
            "CUDA changes the phase and clears the previous download counters");
        Check(Read("Get:1 https://developer.download.nvidia.com/compute/cuda/repos/wsl-ubuntu InRelease [1581 B]", 51).DownloadFileNumber is null,
            "NVIDIA repository metadata does not imply toolkit download progress");
        SpeechSetupProgressSnapshot cudaSize = Read("Need to get 3,348 MB/3,352 MB of archives.", 52);
        Check(cudaSize.DownloadTotalBytes == 3_348_000_000 && cudaSize.Message.Contains("3.3 GB", StringComparison.Ordinal),
            "apt partial-cache syntax uses bytes still required, not the full package size");
        Check(Read("Get:69 https://developer.download.nvidia.com cuda-cublas-13-1 amd64 13.1 [719 MB]", 53).Message.Contains("fetching file 69", StringComparison.Ordinal),
            "a long multi-GB toolkit download shows changing file detail");
        Check(parser.GetSnapshot(start.AddSeconds(54)).Fraction is null, "large package download never invents a percentage");
        Check(Read("Need to get 49.5 kB of archives.", 55).DownloadTotalBytes == 49_500, "decimal kilobytes are parsed using apt's SI units");
        Check(Read("Need to get 1.2 GB of archives.", 56).DownloadTotalBytes == 1_200_000_000, "gigabytes are parsed");
        Check(Read("Need to get 1.2 TB of archives.", 57).DownloadTotalBytes == 1_200_000_000_000, "terabytes are bounded and parsed");
        Check(Read("Need to get 7 B of archives.", 58).DownloadTotalBytes == 7, "byte units are parsed");
        Check(Read("Need to get 0 B/49.5 kB of archives.", 59).Message.Contains("already downloaded", StringComparison.Ordinal), "fully cached packages do not pretend to download");
        Check(Read("Need to get 999999999999999999999999999999999999999999999 TB of archives.", 60).DownloadTotalBytes == 0,
            "out-of-range download sizes leave the last valid requirement intact");
        Check(Read("Need to get not-a-number GB of archives.", 61).DownloadTotalBytes == 0, "malformed sizes are ignored");

        SpeechSetupProgressSnapshot source = Read("Step 3/5: Downloading and verifying the pinned MIT-licensed Voxtral source archive.", 70);
        Check(source.Stage == "Preparing speech files" && source.Fraction is null && source.DownloadTotalBytes is null,
            "source files receive their own phase without inherited package size");
        Check(Read("\u001b[32m  100 8192k  100 8192k    0     0   4096k      0  0:00:02  0:00:02 --:--:-- 4096k\u001b[0m", 71).Fraction is null,
            "curl download completion is not confused with overall setup completion");
        SpeechSetupProgressSnapshot configure = Read("Step 4/5: Configuring the pinned GGML dependency and building with ONE CPU job. No speech model is loaded.", 80);
        Check(configure.StepIndex == 4 && configure.Message == "Configuring the speech engine", "configuration is a visible distinct task");
        Check(Read("[1/9] Creating directories for 'ggml-populate'", 81).Fraction is null, "nested dependency Ninja is not speech compilation progress");
        Check(Read("-- Build files have been written to: /opt/speech/build-cuda/_deps/ggml-subbuild", 82).Fraction is null,
            "nested CMake directories do not unlock main build counters");
        Check(Read("[9/9] Completed 'ggml-populate'", 83).CompletedUnits == 0, "dependency's 100 percent cannot make speech appear finished");
        Check(Read("-- Configuring done (1.3s)", 84).Message == "Configuring the speech engine", "configuration lines retain the functional label");
        Check(Read("-- Build files have been written to: /opt/localtalk-voxtral/pinned/build-cuda", 85).Message == "Compiling the speech engine",
            "top-level CMake completion transitions to compilation");
        SpeechSetupProgressSnapshot build = Read("[14/215] Building CUDA object ggml/src/ggml-cuda/CMakeFiles/ggml-cuda.dir/copy.cu.o", 86);
        Check(build.CompletedUnits == 14 && build.TotalUnits == 215 && Math.Abs(build.Fraction!.Value - 14d / 215) < 1e-12,
            "real completed build tasks yield exact per-build progress");
        Check(build.Message == "Compiling the speech engine · 14 of 215 build tasks", "build detail exposes useful counts without object paths");
        Check(Read("[13/215] Building CXX object delayed", 87).CompletedUnits == 14, "same-build late lines do not rewind progress");
        Check(Read("[999/215] Building CXX object invalid", 88).CompletedUnits == 14, "impossible completed counts are ignored");
        Check(Read("[0/0] Building CXX object invalid", 89).TotalUnits == 215, "zero denominators are ignored");
        Check(Read("[999999999999/215] Building CXX object invalid", 90).CompletedUnits == 14, "overflowing Ninja counts are ignored");
        Check(Read("[15/215] Building CXX object actual", 91).CompletedUnits == 15, "build progress continues after ignored malformed lines");
        Check(Read("warning: suspicious /private/path/user-name", 92).Message.Contains("15 of 215", StringComparison.Ordinal),
            "raw diagnostics stay out of friendly UI details but still count as activity");
        SpeechSetupProgressSnapshot linked = Read("[215/215] Linking CXX executable voxtral-server", 93);
        Check(linked.Fraction == 1 && linked.Message.StartsWith("Finishing", StringComparison.Ordinal), "linking completes only the build phase");
        Check(!linked.ServerMarkerSeen && !linked.ScriptReportedFinished, "a completed compile is not a verified engine or process exit");
        SpeechSetupProgressSnapshot checking = Read("Step 5/5: Checking executable help without a model. GPU inference remains OFF.", 94);
        Check(checking.StepIndex == 5 && checking.Fraction is null && checking.CompletedUnits == 0 && checking.TotalUnits == 0,
            "validation clears build counts and does not claim an overall 100 percent");
        Check(Read("Usage: voxtral-server --model <path>", 95).Message == "Checking the finished speech engine", "raw help text does not replace the useful phase");
        Check(!Read("LOCALTALK_VOXTRAL_SERVER=", 96).ServerMarkerSeen, "empty server marker is not verified output");
        Check(!Read("LOCALTALK_VOXTRAL_SERVER=relative/path", 97).ServerMarkerSeen, "relative server marker is not expected output");
        SpeechSetupProgressSnapshot marker = Read("LOCALTALK_VOXTRAL_SERVER=/opt/localtalk-voxtral/pinned/build-cuda/voxtral-server", 98);
        Check(marker.ServerMarkerSeen && !marker.ScriptReportedFinished && marker.Fraction is null,
            "absolute marker is observed without replacing exit-code validation");
        SpeechSetupProgressSnapshot finished = Read("Speech runtime setup finished. Download/import the realtime model separately, then explicitly press Start.", 99);
        Check(finished.ServerMarkerSeen && finished.ScriptReportedFinished && finished.Fraction is null,
            "helper's finished message is tracked but never presented as whole-app completion");
        Check(finished.Message == "Speech tools prepared; finishing setup", "finalization remains explicit and pending");
        Check(Read("Step 2/5: Installing Vulkan build tools", 100).StepIndex == 5, "late earlier-stage messages cannot rewind the phase");
        Check(parser.GetSnapshot(start.AddSeconds(110)).StageElapsed == TimeSpan.FromSeconds(16) &&
            parser.GetSnapshot(start.AddSeconds(110)).LastActivityAge == TimeSpan.FromSeconds(10), "phase and last output timers remain accurate after late output");
        Check(parser.GetSnapshot(start.AddSeconds(1)).StageElapsed == TimeSpan.Zero && parser.GetSnapshot(start.AddSeconds(1)).LastActivityAge == TimeSpan.Zero,
            "backwards clocks cannot show negative time");

        var cached = new SpeechSetupProgressParser(start);
        cached.Observe("Step 1/5: Installing signed Ubuntu build packages", start);
        SpeechSetupProgressSnapshot skipped = cached.Observe("Step 4/5: Configuring the pinned GGML dependency", start.AddSeconds(1));
        Check(skipped.StepIndex == 4 && skipped.Fraction is null && skipped.StageElapsed == TimeSpan.Zero,
            "cached GPU tools/source can legitimately skip phase markers without fabricated completion");
        cached.Observe("-- Build files have been written to: /opt/speech/build-vulkan/", start.AddSeconds(2));
        Check(cached.Observe("[1/2] Building shader", start.AddSeconds(3)).Fraction == 0.5, "Vulkan top-level paths unlock build progress");
        Check(cached.Observe("ninja: no work to do.", start.AddSeconds(4)).Message == "The speech engine is already built",
            "a reused build has a useful no-work detail");
        var cpu = new SpeechSetupProgressParser(start);
        Check(cpu.Observe("Step 2/5: CPU runtime selected explicitly; realtime speed is not guaranteed.", start).Stage == "Preparing speech support",
            "explicit CPU mode is not mislabeled as a GPU download");
        cpu.Observe("Step 4/5: Configuring", start.AddSeconds(1));
        cpu.Observe("-- Build files have been written to: /opt/speech/build-cpu", start.AddSeconds(2));
        Check(cpu.Observe("[1/1] Linking CXX executable voxtral-server", start.AddSeconds(3)).Fraction == 1, "CPU build counters are also recognized");
        var vulkan = new SpeechSetupProgressParser(start);
        Check(vulkan.Observe("Step 2/5: Installing Vulkan build tools and probing the actual WSL Vulkan device.", start).Stage == "Preparing GPU support",
            "Vulkan tool setup maps to GPU support");
        var ansi = new SpeechSetupProgressParser(start);
        Check(ansi.Observe("\u001b[33mStep 4/5: Configuring\u001b[0m", start).StepIndex == 4, "ANSI coloring does not hide stage markers");
        ansi.Observe("-- Build files have been written to: /opt/speech/build-cuda", start.AddSeconds(1));
        Check(ansi.Observe("\u001b[32m[2/4]\u001b[0m Building CUDA object test", start.AddSeconds(2)).Fraction == 0.5, "ANSI coloring does not hide build counters");
        Check(ansi.Observe(new string('x', 100_000), start.AddSeconds(3)).Message.Contains("2 of 4", StringComparison.Ordinal), "large diagnostic lines are safely bounded");
        Check(ansi.Observe("noise", start.AddSeconds(1)).LastActivityAge == TimeSpan.Zero, "out-of-order activity timestamps are clamped");
        Check(ansi.GetSnapshot(start.AddSeconds(4)).LastActivityAge == TimeSpan.FromSeconds(1), "late timestamps cannot move activity backwards");

        var apt = new SpeechSetupProgressParser(start);
        apt.Observe("Step 2/5: Installing the CUDA 13.1 TOOLKIT", start);
        apt.Observe("Need to get 3,348 MB of archives.", start.AddSeconds(1));
        SpeechSetupProgressSnapshot download = apt.Observe("dlstatus:19:27.5000:Retrieving file 19 of 91: cuda-cublas", start.AddSeconds(2));
        Check(download.Fraction == 0.275 && download.DownloadFileNumber == 19 && download.DownloadTotalBytes == 3_348_000_000,
            "APT download work percentage and ordinal remain separate from its announced total bytes");
        Check(download.CompletedUnits == 0 && download.TotalUnits == 0 && download.Message == "Downloading GPU build tools · 28%",
            "APT aggregate progress is labeled as download work, not completed bytes or build tasks");
        Check(!download.Message.Contains("cuda-cublas", StringComparison.Ordinal), "APT technical package descriptions are hidden in friendly progress");
        Check(apt.Observe("dlstatus:20:100.0:Download complete", start.AddSeconds(3)).Fraction == 1,
            "a completed download transaction can reach 100 percent without completing the script");
        Check(!apt.GetSnapshot(start.AddSeconds(3)).ScriptReportedFinished && !apt.GetSnapshot(start.AddSeconds(3)).ServerMarkerSeen,
            "APT download 100 percent is not speech verification or overall completion");
        SpeechSetupProgressSnapshot installing = apt.Observe("pmstatus:cuda-cublas-13-1:40.0:Installing package: extra description", start.AddSeconds(4));
        Check(installing.Fraction == 0.4 && installing.Message == "Installing GPU build tools · 40%",
            "package-manager transaction has separate current installation progress");
        Check(apt.Observe("Unpacking cuda-cublas-13-1 (13.1) ...", start.AddSeconds(4)).Fraction == 0.4,
            "ordinary unpack text does not flicker a known installation percentage back to unknown");
        Check(apt.Observe("Setting up cuda-cublas-13-1 (13.1) ...", start.AddSeconds(4)).Fraction == 0.4,
            "ordinary configuration text preserves the known installation transaction percentage");
        foreach (string invalid in new[] { "pmstatus:pkg:NaN:bad", "pmstatus:pkg:Infinity:bad", "pmstatus:pkg:-1:bad",
            "pmstatus:pkg:101:bad", "pmstatus:pkg:nonsense:bad", "pmstatus:pkg:50" })
            Check(apt.Observe(invalid, start.AddSeconds(5)).Fraction == 0.4, "invalid or nonfinite APT percentage cannot corrupt progress: " + invalid);
        Check(apt.Observe("pmerror:pkg:100:failed", start.AddSeconds(6)).Fraction == 0.4, "package errors cannot falsely complete the transaction");
        Check(apt.Observe("conffile:/config/path:100:question", start.AddSeconds(7)).Fraction == 0.4, "configuration prompts cannot falsely complete the transaction");
        Check(apt.Observe("media:media-label:100:question", start.AddSeconds(8)).Fraction == 0.4, "media prompts cannot falsely complete the transaction");
        SpeechSetupProgressSnapshot eighty = apt.Observe("pmstatus:pkg:80:Installed", start.AddSeconds(9));
        Check(eighty.Fraction == 0.8 && !eighty.ScriptReportedFinished, "partial final APT installation status is not assumed to finish its phase");
        Check(apt.Observe("Step 3/5: Downloading and verifying", start.AddSeconds(10)).Fraction is null,
            "a new phase clears the previous transaction percentage");
        Check(apt.Observe("pmstatus:late-package:99:late", start.AddSeconds(11)).Fraction is null,
            "late package records outside package phases cannot overwrite source/build progress");
        var aptZero = new SpeechSetupProgressParser(start);
        aptZero.Observe("Step 1/5: Installing signed Ubuntu build packages", start);
        SpeechSetupProgressSnapshot zero = aptZero.Observe("dlstatus:0:0.0000:Starting", start);
        Check(zero.Fraction == 0 && zero.DownloadFileNumber is null && zero.Message == "Downloading build tools · 0%",
            "APT's initial zero is valid work progress but does not display a nonexistent file zero");
        var missingMarker = new SpeechSetupProgressParser(start);
        missingMarker.Observe("Step 5/5: Checking executable help", start);
        SpeechSetupProgressSnapshot announced = missingMarker.Observe("Speech runtime setup finished.", start.AddSeconds(1));
        Check(announced.ScriptReportedFinished && !announced.ServerMarkerSeen && announced.Fraction is null,
            "a helper completion announcement never substitutes for the required server marker");
        return checks;
    }
}
