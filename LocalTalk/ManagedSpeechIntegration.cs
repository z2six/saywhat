using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IncomingTranslator;

// The speech distro is an implementation detail, not another user-facing app.
// This never launches WSL, removes a distro, changes the registry or touches the
// user's Ubuntu. Only verified generated .lnk files are moved to a local backup.
internal static class ManagedSpeechIntegration
{
    internal sealed record Shortcut(string Target, string Arguments);

    internal static bool IsManagedRegistration(WslSpeechRegistration? speech) =>
        LocalTalkProvisioner.IsManagedSpeechRegistration(speech);

    internal static void TryHideGeneratedWindowsShortcuts(WslSpeechRegistration? speech, string dataRoot)
    {
        if (!IsManagedRegistration(speech)) return;
        try
        {
            using RegistryKey? registrations = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (registrations is null) return;
            foreach (string keyName in registrations.GetSubKeyNames())
            {
                if (!Guid.TryParse(keyName, out Guid id)) continue;
                using RegistryKey? registration = registrations.OpenSubKey(keyName);
                if (!string.Equals(registration?.GetValue("DistributionName") as string, speech!.Distribution, StringComparison.Ordinal)) continue;
                // The app's automatic setup uses a modern, per-user WSL storage
                // directory. A name collision or renamed Store distro is not ours.
                string expectedStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wsl", id.ToString("B"));
                if (registration?.GetValue("PackageFamilyName") is not null ||
                    !string.Equals(registration?.GetValue("BasePath") as string, expectedStorage, StringComparison.OrdinalIgnoreCase) ||
                    !IsPlainLocalPath(expectedStorage)) return;
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string backupRoot = Path.Combine(Path.GetFullPath(dataRoot), "hidden-speech-shortcuts");
                int moved = HideVerifiedShortcuts(speech.Distribution, id, startMenu, programs, backupRoot, ReadShortcut);
                if (moved > 0) DesktopLog.Write($"Hidden {moved} internal speech-tool shortcuts. Recoverable copies: {backupRoot}");
                return;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or
            TargetInvocationException or InvalidOperationException or ArgumentException or NotSupportedException)
        { DesktopLog.Write("Internal speech shortcuts could not be hidden; translation remains available. " + error.Message); }
    }

    internal static int HideVerifiedShortcuts(string distribution, Guid id, string startMenu, string programs,
        string backupRoot, Func<string, Shortcut?> readShortcut)
    {
        if (!AppBrand.IsOwnedSpeechDistributionName(distribution) || id == Guid.Empty) return 0;
        string shell = Path.Combine(Path.GetFullPath(startMenu), distribution + ".lnk");
        string guiRoot = Path.Combine(Path.GetFullPath(programs), distribution);
        var candidates = new List<string> { shell };
        if (Directory.Exists(guiRoot) && IsPlainLocalPath(guiRoot))
            candidates.AddRange(Directory.EnumerateFiles(guiRoot, "*.lnk", SearchOption.TopDirectoryOnly)
                .Where(path => IsKnownGuiFileName(Path.GetFileName(path), distribution)).Take(64));
        int moved = 0;
        string? backup = null;
        foreach (string path in candidates)
        {
            if (!File.Exists(path) || !IsPlainLocalPath(path)) continue;
            Shortcut? shortcut = readShortcut(path);
            if (shortcut is null || !IsGeneratedShortcut(Path.GetFileName(path), shortcut, distribution, id)) continue;
            // Refuse redirections before creating anything or moving a shortcut.
            if (!IsPlainLocalPath(backupRoot)) throw new IOException("The shortcut backup folder is redirected or not a local path.");
            backup ??= Path.Combine(Path.GetFullPath(backupRoot), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            if (!IsPlainLocalPath(backup)) throw new IOException("The shortcut backup folder changed unexpectedly.");
            File.Move(path, Path.Combine(backup, Path.GetFileName(path)), overwrite: false);
            moved++;
        }
        // Keep any folder and unfamiliar files in place; no broad recursive delete.
        return moved;
    }

    internal static bool IsGeneratedShortcut(string fileName, Shortcut shortcut, string distribution, Guid id)
    {
        if (!AppBrand.IsOwnedSpeechDistributionName(distribution) || id == Guid.Empty) return false;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string wslDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL");
        bool Target(string name) => string.Equals(shortcut.Target, Path.Combine(wslDirectory, name), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shortcut.Target, Path.Combine(windows, "System32", name), StringComparison.OrdinalIgnoreCase);
        if (fileName == distribution + ".lnk")
            return Target("wsl.exe") && string.Equals(shortcut.Arguments,
                "--distribution-id " + id.ToString("B") + " --cd ~", StringComparison.OrdinalIgnoreCase);
        if (!Target("wslg.exe")) return false;
        string suffix = " (" + distribution + ").lnk";
        if (!IsKnownGuiFileName(fileName, distribution)) return false;
        string display = fileName[..^suffix.Length];
        string prefix = "-d " + distribution + " --cd \"~\" -- ";
        if (!shortcut.Arguments.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string command = shortcut.Arguments[prefix.Length..];
        return display switch
        {
            "Nsight Eclipse Edition" => Regex.IsMatch(command, "^/usr/local/cuda-[0-9]+\\.[0-9]+/bin/nsight$"),
            "NVIDIA Visual Profiler" => Regex.IsMatch(command, "^/usr/local/cuda-[0-9]+\\.[0-9]+/bin/nvvp$"),
            _ when Regex.IsMatch(display, "^NVIDIA Nsight Compute [0-9]+\\.[0-9]+\\.[0-9]+$") =>
                Regex.IsMatch(command, "^/opt/nvidia/nsight-compute/[0-9]+\\.[0-9]+\\.[0-9]+/host/linux-desktop-glibc_[0-9_]+-x64/ncu-ui$"),
            _ when Regex.IsMatch(display, "^NVIDIA Nsight Systems [0-9]+\\.[0-9]+\\.[0-9]+$") =>
                Regex.IsMatch(command, "^/opt/nvidia/nsight-systems/[0-9]+\\.[0-9]+\\.[0-9]+/host-linux-x64/nsys-ui$"),
            _ => false
        };
    }

    private static bool IsKnownGuiFileName(string fileName, string distribution)
    {
        string suffix = " (" + distribution + ").lnk";
        if (!fileName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string name = fileName[..^suffix.Length];
        return name is "Nsight Eclipse Edition" or "NVIDIA Visual Profiler" ||
            Regex.IsMatch(name, "^NVIDIA Nsight (Compute|Systems) [0-9]+\\.[0-9]+\\.[0-9]+$");
    }

    private static Shortcut? ReadShortcut(string path)
    {
        object? shell = null, link = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            if (link is null) return null;
            Type linkType = link.GetType();
            string? target = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            string? arguments = linkType.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string;
            return target is null || arguments is null ? null : new(target, arguments);
            // No Save() and no process execution: parsing a .lnk does not launch it.
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool IsPlainLocalPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\') return false;
        for (string? ancestor = full; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            // GetAttributes sees dangling links too; Exists follows the target
            // and could miss a redirect just before Directory.CreateDirectory.
            try { if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }
}
