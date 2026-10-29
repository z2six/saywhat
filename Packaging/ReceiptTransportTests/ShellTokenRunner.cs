using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

// This broker has one executable target: Windows' own PowerShell. It never
// elevates, loads a profile, runs an installer or launches an app executable.
internal sealed class ShellTokenRunner : IDisposable
{
    private readonly SafeAccessTokenHandle _primary;
    private readonly string _workingDirectory, _syntheticRoot;
    internal string Sid { get; }
    internal string Account { get; }
    internal bool CallerElevated { get; }
    internal string PowerShellPath { get; }
    internal List<ChildExecution> Executions { get; } = [];

    internal ShellTokenRunner(string workingDirectory, string syntheticRoot)
    {
        _workingDirectory = workingDirectory;
        _syntheticRoot = syntheticRoot;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        PowerShellPath = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(PowerShellPath)) throw new InvalidOperationException("Trusted Windows PowerShell is missing.");
        using var current = WindowsIdentity.GetCurrent();
        CallerElevated = IsElevated(current.AccessToken);
        IntPtr window = GetShellWindow();
        if (window == IntPtr.Zero) throw new InvalidOperationException("An interactive Explorer shell is required.");
        GetWindowThreadProcessId(window, out uint id);
        using var shell = Process.GetProcessById(checked((int)id));
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid || !OpenProcessToken(process, 0x000B, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            using var shellIdentity = new WindowsIdentity(token.DangerousGetHandle());
            Sid = shellIdentity.User?.Value ?? "";
            Account = shellIdentity.Name;
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || Sid != current.User?.Value || IsElevated(token) ||
                shell.SessionId != Process.GetCurrentProcess().SessionId ||
                !Path.GetFullPath(path.ToString()).Equals(Path.Combine(windows, "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A same-account, same-session, unelevated Windows Explorer token could not be verified.");
            if (!DuplicateTokenEx(token, 0x000B, IntPtr.Zero, 2, 1, out _primary)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal ChildExecution RunLimited(string script, params string[] arguments)
        => RunLimitedCore(script, null, null, arguments);

    internal ChildExecution RunLimitedWithInheritedTemp(string script, string inheritedTemp, params string[] arguments)
    {
        string resolved = Path.GetFullPath(inheritedTemp);
        if (!resolved.StartsWith(_workingDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(resolved) || File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Inherited temp regression must target an existing plain directory in this exact fixture.");
        return RunLimitedCore(script, resolved, null, arguments);
    }

    internal ChildExecution RunLimitedWithInheritedModules(string script, string inheritedModules, params string[] arguments)
    {
        string resolved = Path.GetFullPath(inheritedModules);
        if (!resolved.StartsWith(_workingDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(resolved) || File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Inherited module regression must target an existing plain directory in this exact fixture.");
        return RunLimitedCore(script, null, resolved, arguments);
    }

    private ChildExecution RunLimitedCore(string script, string? inheritedTemp, string? inheritedModules, string[] arguments)
    {
        string command = BuildCommand(script, arguments);
        if (!CallerElevated)
        {
            // In default mode the verified caller already IS the limited shell
            // account. No privilege broker or fallback to an elevated token.
            return RunCurrent(script, arguments, requireLimited: true, inheritedTemp: inheritedTemp, inheritedModules: inheritedModules);
        }
        if (!CreateEnvironmentBlock(out IntPtr inheritedEnvironment, _primary, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr environment = IntPtr.Zero;
        try
        {
            environment = BuildEnvironment(inheritedEnvironment, inheritedTemp, inheritedModules);
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), flags = 1, show = 0 };
            const uint flags = 0x08000400; // CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT
            bool started = CreateProcessAsUserW(_primary, PowerShellPath, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero,
                false, flags, environment, _workingDirectory, ref startup, out ProcessInformation created);
            string broker = "CreateProcessAsUser";
            if (!started)
            {
                int asUserError = Marshal.GetLastWin32Error();
                if (asUserError is not (5 or 1314)) throw new Win32Exception(asUserError);
                // Same verified primary token; logon flags zero avoid profile
                // loading. Explicit application name prevents executable search.
                started = CreateProcessWithTokenW(_primary, 0, PowerShellPath, new StringBuilder(command), flags,
                    environment, _workingDirectory, ref startup, out created);
                broker = "CreateProcessWithToken";
            }
            if (!started) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the verified limited shell token.");
            CloseHandle(created.Thread);
            using var child = new SafeProcessHandle(created.Process, true);
            if (!OpenProcessToken(child, 8, out var childToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (childToken)
            {
                using var identity = new WindowsIdentity(childToken.DangerousGetHandle());
                if (identity.User?.Value != Sid || IsElevated(childToken))
                    throw new InvalidOperationException("The launched helper was not the verified limited account.");
            }
            if (WaitForSingleObject(child, 30_000) != 0)
                throw new TimeoutException("The limited metadata helper exceeded 30 seconds. No process was stopped.");
            if (!GetExitCodeProcess(child, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new ChildExecution(Path.GetFileName(script), checked((int)exitCode), Sid, false, broker, "", "");
            Executions.Add(result);
            return result;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            DestroyEnvironmentBlock(inheritedEnvironment);
        }
    }

    internal ChildExecution RunCurrent(string script, string[] arguments, bool requireLimited = false, string? inheritedTemp = null, string? inheritedModules = null)
    {
        if (requireLimited && CallerElevated) throw new InvalidOperationException("An elevated fallback is forbidden.");
        _ = BuildCommand(script, arguments);
        var info = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _workingDirectory
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["SAYWHAT_DATA_ROOT"] = _syntheticRoot;
        info.Environment["LOCALTALK_DATA_ROOT"] = _syntheticRoot;
        info.Environment["TEMP"] = inheritedTemp ?? Path.Combine(_workingDirectory, "runtime-temp");
        info.Environment["TMP"] = inheritedTemp ?? Path.Combine(_workingDirectory, "runtime-temp");
        info.Environment["PSModulePath"] = inheritedModules ?? Path.Combine(Path.GetDirectoryName(PowerShellPath)!, "Modules");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) throw new TimeoutException("The metadata helper exceeded 30 seconds. No process was stopped.");
        var result = new ChildExecution(Path.GetFileName(script), process.ExitCode, Sid, CallerElevated, "VerifiedCurrentToken",
            output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        Executions.Add(result);
        return result;
    }

    private string BuildCommand(string script, IEnumerable<string> arguments)
    {
        string resolved = Path.GetFullPath(script);
        if (!File.Exists(resolved) || !resolved.StartsWith(_workingDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a copied script within this unique fixture may run.");
        string[] all = [PowerShellPath, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", resolved, .. arguments];
        if (all.Any(value => value.Contains('"') || value.Contains('\0') || value.Contains('\r') || value.Contains('\n')))
            throw new InvalidOperationException("Unexpected characters in the fixed helper command.");
        string command = string.Join(' ', all.Select(value => '"' + value + '"'));
        if (command.Length >= 1024) throw new InvalidOperationException("The limited helper command exceeds the Windows token-launch bound.");
        return command;
    }

    private IntPtr BuildEnvironment(IntPtr source, string? inheritedTemp, string? inheritedModules)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        while (Marshal.ReadInt16(source, offset) != 0)
        {
            string entry = Marshal.PtrToStringUni(IntPtr.Add(source, offset))!;
            offset += (entry.Length + 1) * 2;
            int separator = entry.IndexOf('=', entry.StartsWith('=') ? 1 : 0);
            if (separator > 0) values[entry[..separator]] = entry[(separator + 1)..];
        }
        values["SAYWHAT_DATA_ROOT"] = _syntheticRoot;
        values["LOCALTALK_DATA_ROOT"] = _syntheticRoot;
        values["TEMP"] = inheritedTemp ?? Path.Combine(_workingDirectory, "runtime-temp");
        values["TMP"] = inheritedTemp ?? Path.Combine(_workingDirectory, "runtime-temp");
        values["PSModulePath"] = inheritedModules ?? Path.Combine(Path.GetDirectoryName(PowerShellPath)!, "Modules");
        string block = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out int value, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value != 0;
    }
    internal static (int Before, int During, int After) ProbeDirectoryPin(string path)
    {
        int DeleteOpenError()
        {
            using var probe = CreateFileW(path, 0x10000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            return probe.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
        int before = DeleteOpenError();
        int during;
        using (var pin = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            if (pin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The fixture directory pin could not be opened.");
            during = DeleteOpenError();
        }
        return (before, during, DeleteOpenError());
    }
    public void Dispose() => _primary.Dispose();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title; public int x, y, width, height, xCount, yCount, fill, flags;
        public short show, reservedLength; public IntPtr reservedData, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int information, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle primary);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation created);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logon, string app, StringBuilder command, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation created);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

internal sealed record ChildExecution(string Script, int ExitCode, string UserSid, bool Elevated, string Broker, string Output, string Error);
