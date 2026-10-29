#Requires -Version 5.1
<#
Installer/uninstaller data choices. All switches are opt-in; no choice means keep.
Never removes a data root, follows a link, scans another model library, launches
WSL/inference, stops a process, unregisters a distro or changes provisioning.json.
Receipts contain only operation metadata, never the contents of settings or logs.
#>
[CmdletBinding()]
param(
    [string]$DataRoot,
    [switch]$ResetPreferences,
    [switch]$ClearHistory,
    [switch]$DeleteCachedModels,
    [switch]$ValidateOnly,
    [switch]$ResolveUserIdentity,
    [switch]$RequirePrivateUserRoot,
    [string]$ExpectedUserSid,
    [switch]$PrepareProtectedReceipt,
    [switch]$RequireProtectedReceipt,
    [switch]$RequireOriginalUserToken,
    [string]$ReceiptNonce,
    [Parameter(Mandatory=$true)][string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This installer helper uses Windows PowerShell's built-in modules only. A
# caller may inherit an incompatible app/runtime PSModulePath; elevated metadata
# preparation must also never discover or execute user-supplied modules.
[Environment]::SetEnvironmentVariable('PSModulePath',[IO.Path]::Combine($PSHOME,'Modules'),[EnvironmentVariableTarget]::Process)

function Initialize-OriginalUserRuntimeTemp {
    if (!$RequireProtectedReceipt -or $PrepareProtectedReceipt) { return }
    # Inno can pass its protected setup directory as TEMP/TMP to this limited
    # child. Even PowerShell module loading needs writable runtime scratch space.
    # Use Windows' known folder, never the inherited environment or receipt data.
    # This changes only this child process, not any account/environment setting.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return }
        if ($ExpectedUserSid -notmatch '^S-1-5-21-[0-9-]+$' -or $identity.User.Value -ne $ExpectedUserSid) {
            throw 'The original Windows account is not running this check. Personal data is kept.'
        }
        $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($local) -or $local -notmatch '^[A-Za-z]:[\\/]') {
            throw 'The Windows runtime temporary folder could not be verified. Personal data is kept.'
        }
        $runtimeTemp = [IO.Path]::GetFullPath([IO.Path]::Combine($local,'Temp'))
        if (![IO.Directory]::Exists($runtimeTemp)) {
            throw 'The Windows runtime temporary folder is missing. Personal data is kept.'
        }
        # No module autoload, directory creation, ACL change or reparse traversal
        # is allowed during this bootstrap. Elevated branches never use it.
        $ancestor = $runtimeTemp
        while ($ancestor) {
            if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The Windows runtime temporary folder contains a link. Personal data is kept.'
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        [Environment]::SetEnvironmentVariable('TEMP',$runtimeTemp,[EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('TMP',$runtimeTemp,[EnvironmentVariableTarget]::Process)
    }
    finally { $identity.Dispose() }
}

# Run before New-Object/Get-Acl/CIM/Add-Type can trigger module/runtime loading.
# A failed bootstrap must not retry the same unavailable module to write a receipt.
try { Initialize-OriginalUserRuntimeTemp }
catch { [Console]::Error.WriteLine('Data-safety runtime initialization failed: ' + $_.Exception.Message); exit 1 }

$mutex = $null
$ownsMutex = $false
$quarantine = $null
$moved = New-Object 'System.Collections.Generic.List[object]'
$deletedCount = 0
$settingsRewritten = $false
$commitStarted = $false

function Initialize-ReceiptIO {
    if ('SayWhatReceiptIO' -as [type]) { return }
    # No-follow, exclusive handles prevent receipt reparse changes between the
    # attribute check and read/truncate/write. No native executable is launched.
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class SayWhatReceiptIO {
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh,
            WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string p,uint a,uint s,IntPtr sa,uint c,uint f,IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
    public static FileStream OpenExisting(string path,bool write) {
        var handle=CreateFileW(path,write ? 0x40000000u : 0x80000000u,write ? 0u : 1u,
            IntPtr.Zero,3,0x00200000,IntPtr.Zero);
        if(handle.IsInvalid) { int e=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(e); }
        Info info;
        if(!GetFileInformationByHandle(handle,out info)) { int e=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(e); }
        if((info.Attributes & (0x400u|0x10u))!=0) { handle.Dispose(); throw new IOException("The protected receipt is a link or directory."); }
        try { return new FileStream(handle,write ? FileAccess.Write : FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
}
'@
}

function Initialize-ProcessMetadata {
    if ('SayWhatInstallerProcessMetadata' -as [type]) { return }
    # Read-only kernel/token metadata avoids a dependency on the optional WMI
    # service. Relevant processes that cannot be inspected still block cleanup.
    # No privilege is enabled, process is stopped, or remote memory is accessed.
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public sealed class SayWhatInstallerProcessInfo {
    public string Name { get; internal set; }
    public uint ProcessId { get; internal set; }
    public uint SessionId { get; internal set; }
    public ulong CreationFileTime { get; internal set; }
    public string ExecutablePath { get; internal set; }
    public string CommandLine { get; internal set; }
    public string Sid { get; internal set; }
    public int ReturnValue { get { return 0; } }
}
public static class SayWhatInstallerProcessMetadata {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Entry {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    sealed class SnapshotChanged : Exception { }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int QueryProcess(SafeProcessHandle handle,int informationClass,IntPtr buffer,int size,out int returned);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags,uint process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32FirstW(SafeFileHandle snapshot,ref Entry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32NextW(SafeFileHandle snapshot,ref Entry entry);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process,out FileTime created,out FileTime exited,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ProcessIdToSessionId(uint process,out uint session);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder name,ref int length);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,out uint value,int length,out int returned);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true,SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
    static Entry[] Snapshot() {
        using(var handle=CreateToolhelp32Snapshot(2,0)) {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var entry=new Entry { Size=(uint)Marshal.SizeOf(typeof(Entry)) };
            if(!Process32FirstW(handle,ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var results=new List<Entry>();
            do {
                if(results.Count>=131072) throw new IOException("The process snapshot exceeded its safe size limit.");
                results.Add(entry); entry.Size=(uint)Marshal.SizeOf(typeof(Entry));
            } while(Process32NextW(handle,ref entry));
            int error=Marshal.GetLastWin32Error();
            if(error!=18) throw new Win32Exception(error);
            return results.ToArray();
        }
    }
    static bool Alive(SafeProcessHandle handle) {
        uint status;
        if(!GetExitCodeProcess(handle,out status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        FileTime created,exited,kernel,user;
        if(!GetProcessTimes(handle,out created,out exited,out kernel,out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // A program may itself exit with code STILL_ACTIVE (259). Its exit
        // timestamp distinguishes that case without requesting extra rights.
        return status==259 && exited.Low==0 && exited.High==0;
    }
    static string CommandLine(SafeProcessHandle handle) {
        // Class 60 is ProcessCommandLineInformation. Class 26 is WOW64 and
        // cannot be used here. Resolve the optional NT entry point at runtime.
        IntPtr module=GetModuleHandleW("ntdll.dll");
        IntPtr address=module==IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module,"NtQueryInformationProcess");
        if(address==IntPtr.Zero) throw new IOException("Windows process command-line inspection is unavailable. Personal data is kept.");
        var query=(QueryProcess)Marshal.GetDelegateForFunctionPointer(address,typeof(QueryProcess));
        int size=0;
        int status=query(handle,60,IntPtr.Zero,0,out size);
        if(status!=unchecked((int)0xC0000004) && status!=unchecked((int)0xC0000023) && status!=unchecked((int)0x80000005))
            throw new IOException("A relevant process command line could not be verified. Personal data is kept.");
        int header=Marshal.SizeOf(typeof(UnicodeString));
        for(int attempt=0;attempt<3;attempt++) {
            if(size<header || size>131072) throw new IOException("A process command line exceeded its safe size limit.");
            int capacity=size;
            IntPtr buffer=Marshal.AllocHGlobal(capacity);
            try {
                status=query(handle,60,buffer,capacity,out size);
                if(status==unchecked((int)0xC0000004) || status==unchecked((int)0xC0000023) || status==unchecked((int)0x80000005)) continue;
                if(status<0 || size<header || size>capacity) throw new IOException("A relevant process command line could not be verified. Personal data is kept.");
                var text=(UnicodeString)Marshal.PtrToStructure(buffer,typeof(UnicodeString));
                long offset=text.Buffer.ToInt64()-buffer.ToInt64();
                if((text.Length&1)!=0 || (text.MaximumLength&1)!=0 || text.MaximumLength<text.Length || offset<header || offset>capacity || text.MaximumLength>capacity-offset)
                    throw new IOException("Windows returned an invalid process command-line buffer.");
                string value=Marshal.PtrToStringUni(text.Buffer,text.Length/2);
                if(String.IsNullOrWhiteSpace(value)) throw new IOException("A relevant process command line is unavailable. Personal data is kept.");
                return value;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("The process command line changed repeatedly. Personal data is kept.");
    }
    static SayWhatInstallerProcessInfo Inspect(Entry entry,bool commandLine,uint? requiredShellSession) {
        using(var handle=OpenProcess(0x1000,false,entry.Pid)) {
            if(handle.IsInvalid) {
                int error=Marshal.GetLastWin32Error();
                if(error==87) throw new SnapshotChanged();
                throw new Win32Exception(error,"A relevant running process could not be verified. Personal data is kept.");
            }
            if(!Alive(handle)) return null;
            var path=new StringBuilder(32768); int length=path.Capacity;
            if(!QueryFullProcessImageNameW(handle,0,path,ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!String.Equals(Path.GetFileName(path.ToString()),entry.Name,StringComparison.OrdinalIgnoreCase)) throw new SnapshotChanged();
            FileTime created,exited,kernel,user;
            if(!GetProcessTimes(handle,out created,out exited,out kernel,out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint session;
            if(!ProcessIdToSessionId(entry.Pid,out session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!Alive(handle)) return null;
            if(requiredShellSession.HasValue && requiredShellSession.Value!=session) return null;
            var result=new SayWhatInstallerProcessInfo { Name=entry.Name,ProcessId=entry.Pid,SessionId=session,
                CreationFileTime=((ulong)created.High<<32)|created.Low,ExecutablePath=path.ToString() };
            if(requiredShellSession.HasValue) {
                SafeAccessTokenHandle token;
                if(!OpenProcessToken(handle,8,out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using(token) {
                    uint tokenSession; int returned;
                    if(!GetTokenInformation(token,12,out tokenSession,4,out returned) || returned!=4)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if(tokenSession!=session) throw new IOException("The interactive shell session changed. Personal data is kept.");
                    using(var identity=new WindowsIdentity(token.DangerousGetHandle())) {
                        if(identity.User==null) throw new IOException("The interactive shell owner is unavailable. Personal data is kept.");
                        result.Sid=identity.User.Value;
                    }
                }
            }
            if(commandLine) result.CommandLine=CommandLine(handle);
            if(!Alive(handle)) return null;
            return result;
        }
    }
    static bool AppName(string name) {
        switch(name.ToLowerInvariant()) {
            case "saywhat.exe": case "localtalk.exe": case "vrchatincomingtranslator.exe": case "incomingtranslator.exe":
            case "foxtrans.exe": case "foxtrans.localhost.exe": case "foxtrans.cli.exe": case "foxtrans.desktop.exe": return true;
            default: return false;
        }
    }
    public static SayWhatInstallerProcessInfo[] ShellOwners(uint session) {
        for(int attempt=0;attempt<3;attempt++) {
            try {
                var owners=new List<SayWhatInstallerProcessInfo>();
                foreach(var entry in Snapshot()) if(String.Equals(entry.Name,"explorer.exe",StringComparison.OrdinalIgnoreCase)) {
                    var owner=Inspect(entry,false,session);
                    if(owner!=null) owners.Add(owner);
                }
                return owners.ToArray();
            } catch(SnapshotChanged) { }
        }
        throw new IOException("The interactive shell changed repeatedly. Personal data is kept.");
    }
    public static SayWhatInstallerProcessInfo[] RelevantProcesses() {
        uint self=GetCurrentProcessId();
        for(int attempt=0;attempt<3;attempt++) {
            try {
                var results=new List<SayWhatInstallerProcessInfo>();
                foreach(var entry in Snapshot()) {
                    if(entry.Pid==self) continue;
                    string name=entry.Name.ToLowerInvariant();
                    bool app=AppName(name);
                    bool detail=name=="powershell.exe" || name=="pwsh.exe" || name=="wsl.exe" || name=="llama-server.exe" || name=="nemo-speech.exe";
                    if(!app && !detail) continue;
                    var process=Inspect(entry,detail,null);
                    if(process!=null) results.Add(process);
                }
                return results.ToArray();
            } catch(SnapshotChanged) { }
        }
        throw new IOException("Relevant processes changed repeatedly. Personal data is kept.");
    }
    public static SayWhatInstallerProcessInfo CurrentProcessForDiagnostics() {
        uint self=GetCurrentProcessId();
        foreach(var entry in Snapshot()) if(entry.Pid==self) {
            var result=Inspect(entry,true,null);
            if(result!=null) return result;
        }
        throw new IOException("The current process metadata could not be verified.");
    }
    public static bool CurrentTokenIsElevated() {
        using(var process=OpenProcess(0x1000,false,GetCurrentProcessId())) {
            if(process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeAccessTokenHandle token;
            if(!OpenProcessToken(process,8,out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using(token) {
                uint elevated; int returned;
                if(!GetTokenInformation(token,20,out elevated,4,out returned) || returned!=4)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if(elevated>1) throw new IOException("The Windows token elevation state could not be verified.");
                return elevated==1;
            }
        }
    }
    public static int[] TcpListenerPorts() {
        // The framework's documented listener API reads native IP Helper tables
        // for IPv4 and IPv6. It does not call the CIM/WMI networking provider.
        var endpoints=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        if(endpoints==null || endpoints.Length>1048576) throw new IOException("Local TCP listener metadata could not be verified.");
        var ports=new HashSet<int>();
        foreach(var endpoint in endpoints) ports.Add(endpoint.Port);
        var result=new int[ports.Count]; ports.CopyTo(result); return result;
    }
}
'@
}

function Get-ProtectedReceiptSid {
    if ($ReceiptNonce -notmatch '^[a-fA-F0-9]{32}$') { throw 'The protected receipt request identifier is invalid.' }
    $path=[IO.Path]::GetFullPath($ReceiptPath)
    if ([IO.Path]::GetFileName($path) -notin @('saywhat-data-identity.ini','saywhat-data-actions.ini')) { throw 'Only the two fixed installer receipt files can be prepared.' }
    $parent=[IO.Path]::GetDirectoryName($path)
    if ([IO.Path]::GetFileName($parent) -notmatch '^is-[a-zA-Z0-9]{10}([.]tmp|-uninstall[.]tmp)$') { throw 'A protected installer temporary folder is required.' }
    Assert-PlainPath $path
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'The protected installer temporary folder is missing.' }
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($parent)).DriveFormat -ne 'NTFS') { throw 'Protected installer receipts require NTFS.' }
    $acl=Get-Acl -LiteralPath $parent
    $privileged=@('S-1-5-18','S-1-5-32-544')
    if (!$acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $privileged) { throw 'The receipt parent is not an administrator-protected installer folder.' }
    $users=New-Object 'System.Collections.Generic.HashSet[string]'
    $read=[int64]([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize)
    foreach ($ace in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($ace.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { throw 'The protected receipt folder has an unsupported access rule.' }
        $sid=$ace.IdentityReference.Value
        if ($sid -in $privileged) { continue }
        if ($sid -notmatch '^S-1-5-21-[0-9-]+$' -or (([int64]$ace.FileSystemRights -band (-bnot $read)) -ne 0)) { throw 'The receipt parent grants unsupported access. No permissions were changed.' }
        [void]$users.Add($sid)
    }
    if ($users.Count -ne 1) { throw 'One original Windows account must have read-only access to the protected temporary folder.' }
    $userSid=@($users)[0]
    # The executing script stays protected; only the fixed result leaves change.
    if (![IO.Path]::GetDirectoryName($PSCommandPath).Equals($parent,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($PSCommandPath) -notin @('manage-install-data.ps1','saywhat-uninstall-data.ps1')) { throw 'The protected helper must be in the same installer temporary folder.' }
    $helperAcl=Get-Acl -LiteralPath $PSCommandPath
    if ($helperAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $privileged) { throw 'The protected installer helper owner is invalid.' }
    foreach ($ace in $helperAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($ace.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $ace.IdentityReference.Value -notin $privileged -and
            (($ace.IdentityReference.Value -ne $userSid) -or (([int64]$ace.FileSystemRights -band (-bnot $read)) -ne 0))) { throw 'The installer helper is writable by an unprivileged account. No permissions were changed.' }
    }
    return $userSid
}

function Assert-ProtectedReceipt {
    $expectedLeaf=if ($ResolveUserIdentity) { 'saywhat-data-identity.ini' } else { 'saywhat-data-actions.ini' }
    if ([IO.Path]::GetFileName($ReceiptPath) -ne $expectedLeaf) { throw 'The protected receipt does not match this operation.' }
    $sid=Get-ProtectedReceiptSid
    if ($ExpectedUserSid -ne $sid) { throw 'The selected Windows account does not match the protected receipt grant.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($identity.User.Value -ne $sid) { throw 'The original Windows account is not running this check. Personal data is kept.' }
        if ($RequireOriginalUserToken -and [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The original non-elevated Windows account is unavailable. Personal data is kept.' }
    } finally { $identity.Dispose() }
    Assert-CurrentInteractiveUser $sid
    $acl=Get-Acl -LiteralPath $ReceiptPath
    if (!$acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'The protected receipt owner or permissions changed.' }
    $allowed=[int64]([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Synchronize)
    $granted=$false
    foreach ($ace in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($ace.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { throw 'The protected receipt has an unsupported access rule.' }
        if ($ace.IdentityReference.Value -in @('S-1-5-18','S-1-5-32-544')) { continue }
        if ($ace.IdentityReference.Value -ne $sid -or (([int64]$ace.FileSystemRights -band (-bnot $allowed)) -ne 0)) { throw 'The protected receipt grants unsupported access.' }
        if (($ace.FileSystemRights -band [Security.AccessControl.FileSystemRights]::WriteData) -ne 0) { $granted=$true }
    }
    if (!$granted) { throw 'The original account cannot write this protected receipt.' }
    Initialize-ReceiptIO
    $reader=$null
    $stream=[SayWhatReceiptIO]::OpenExisting($ReceiptPath,$false)
    try {
        if ($stream.Length -gt 131072) { throw 'The protected receipt is too large.' }
        $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::Unicode,$true); $text=$reader.ReadToEnd()
    }
    finally { if ($reader) { $reader.Dispose() }; $stream.Dispose() }
    if ($text.Length -gt 65536 -or $text -notmatch ('(?m)^ReceiptNonce='+[regex]::Escape($ReceiptNonce)+'\r?$')) { throw 'The protected receipt request changed.' }
    return $sid
}

function Write-ReceiptText([string]$Text) {
    if (!$RequireProtectedReceipt) { [IO.File]::WriteAllText($ReceiptPath,$Text,[Text.Encoding]::Unicode); return }
    $sid=Assert-ProtectedReceipt
    # Derive this header from protected ACL/current-token checks, never copy
    # writable receipt metadata as authority for an account or path.
    $Text="[Transport]`r`nUserSid=$sid`r`nReceiptNonce=$ReceiptNonce`r`n"+$Text
    $stream=[SayWhatReceiptIO]::OpenExisting($ReceiptPath,$true)
    try {
        $stream.SetLength(0)
        $bytes=[Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($Text)
        $stream.Write($bytes,0,$bytes.Length); $stream.Flush()
    } finally { $stream.Dispose() }
}

function Prepare-ProtectedReceipt {
    if ($ResetPreferences -or $ClearHistory -or $DeleteCachedModels -or $ValidateOnly -or $ResolveUserIdentity -or $RequirePrivateUserRoot -or $RequireProtectedReceipt -or $ExpectedUserSid) { throw 'Receipt preparation cannot apply or validate data changes.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { if (![Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Only elevated metadata preparation can create a protected receipt.' } }
    finally { $identity.Dispose() }
    $sid=Get-ProtectedReceiptSid
    if (Test-Path -LiteralPath $ReceiptPath) {
        if (!(Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) { throw 'The fixed receipt leaf is not a file.' }
        # DeleteFile removes the exact leaf itself, never a linked target. Its
        # protected parent disallows non-admin creation/replacement/deletion.
        [IO.File]::Delete($ReceiptPath)
    }
    $stream=[IO.File]::Open($ReceiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $acl=New-Object Security.AccessControl.FileSecurity
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        $acl.SetAccessRuleProtection($true,$false)
        foreach ($privileged in @('S-1-5-18','S-1-5-32-544')) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($privileged),[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow)) }
        $rights=[Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Write
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),$rights,[Security.AccessControl.AccessControlType]::Allow))
        [IO.File]::SetAccessControl($ReceiptPath,$acl)
        $text="[Transport]`r`nUserSid=$sid`r`nReceiptNonce=$ReceiptNonce`r`n[Result]`r`nSuccess=0`r`nMessage=The protected data-safety check has not completed.`r`nReceiptNonce=$ReceiptNonce`r`n"
        $bytes=[Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($text)
        $stream.Write($bytes,0,$bytes.Length); $stream.Flush()
    } finally { $stream.Dispose() }
}

function Resolve-PrivateRootSelection([string]$LocalAppData,[string]$Configured,[string]$LegacyConfigured) {
    $local = [IO.Path]::GetFullPath($LocalAppData).TrimEnd([char[]]@('\','/'))
    if ($local -notmatch '^[A-Za-z]:[\\/]') { throw 'The private Windows data folder could not be verified. Personal data is kept.' }
    $current = Join-Path $local 'SayWhat'; $legacy = Join-Path $local 'LocalTalk'
    if ([string]::IsNullOrWhiteSpace($Configured)) { $Configured = $LegacyConfigured }
    $root = if (![string]::IsNullOrWhiteSpace($Configured)) { [IO.Path]::GetFullPath($Configured) }
        elseif (Test-Path -LiteralPath $current -PathType Container) { $current }
        elseif (Test-Path -LiteralPath $legacy -PathType Container) { $legacy } else { $current }
    $root = $root.TrimEnd([char[]]@('\','/'))
    if ($root -match '[\x00-\x1f]' -or $root.Contains('"')) { throw 'The app-data folder is not supported. Personal data is kept.' }
    $supported = $root.Equals($current,[StringComparison]::OrdinalIgnoreCase) -or $root.Equals($legacy,[StringComparison]::OrdinalIgnoreCase)
    return [pscustomobject]@{ Root=$root; LocalAppData=$local; Supported=$supported }
}

function Assert-InteractiveOwners([string]$UserSid,[object[]]$Owners) {
    if ($Owners.Count -eq 0) { throw 'The initiating Windows account could not be verified. Personal data is kept.' }
    foreach ($owner in $Owners) {
        if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne $UserSid) {
            throw 'Setup is running as a different Windows account. Personal data is kept; start setup normally from the account whose data you want to change.'
        }
    }
}

function Get-PrivateUserIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $identity.User.Value; $account = $identity.Name }
    finally { $identity.Dispose() }
    Assert-CurrentInteractiveUser $sid
    $selection = Resolve-PrivateRootSelection ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        ([Environment]::GetEnvironmentVariable('SAYWHAT_DATA_ROOT')) ([Environment]::GetEnvironmentVariable('LOCALTALK_DATA_ROOT'))
    return [pscustomobject]@{ Sid=$sid; Account=$account; Root=$selection.Root; LocalAppData=$selection.LocalAppData; Supported=$selection.Supported }
}

function Assert-CurrentInteractiveUser([string]$Sid) {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    # Never guess from USERNAME, a profile-directory name, or the elevated HKCU.
    # The initiating setup token must also belong to this interactive session's
    # shell owner. This detects alternate UAC credentials and elevated fallback.
    Initialize-ProcessMetadata
    $owners = @([SayWhatInstallerProcessMetadata]::ShellOwners([uint32]$session))
    Assert-InteractiveOwners $sid $owners
}

function Resolve-UserIdentityReceipt {
    # Resolution creates only the installer-private receipt, never an app folder.
    # No selected change may be combined with this metadata-only mode.
    try {
        if ($ResetPreferences -or $ClearHistory -or $DeleteCachedModels -or $ValidateOnly -or $RequirePrivateUserRoot -or ($ExpectedUserSid -and !$RequireProtectedReceipt)) { throw 'Identity resolution cannot apply data changes.' }
        if ($RequireProtectedReceipt) { [void](Assert-ProtectedReceipt) }
        $user = Get-PrivateUserIdentity
        $receipt = [IO.Path]::GetFullPath($ReceiptPath)
        if ($receipt -eq $user.Root -or $receipt.StartsWith($user.Root+'\',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'The identity receipt must be outside the app-data folder.'
        }
        Assert-PlainPath $receipt
        if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($receipt)) -PathType Container)) { throw 'The identity receipt folder is missing.' }
        $supportedValue = if ($user.Supported) { '1' } else { '0' }
        $text = "[Result]`r`nSuccess=1`r`nUserSid=$($user.Sid)`r`nAccount=$($user.Account)`r`nDataRoot=$($user.Root)`r`nSupported=$supportedValue`r`nReceiptNonce=$ReceiptNonce`r`n"
        Write-ReceiptText $text
        exit 0
    }
    catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
}

function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Data cleanup was cancelled because a selected folder or file is a link. No linked location will be removed.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd([char[]]@('\','/')))
        if (!$parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Resolve-SafeRoot([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '[\x00-\x1f]' -or $Path.Contains('"')) {
        throw 'Data cleanup requires the app folder on a local Windows drive.'
    }
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\','/'))
    if ([IO.Path]::GetFileName($resolved) -notin @('SayWhat','LocalTalk')) {
        throw 'Data cleanup was cancelled: the selected location is not a SayWhat app-data folder.'
    }
    Assert-PlainPath $resolved
    if ((Test-Path -LiteralPath $resolved) -and !(Test-Path -LiteralPath $resolved -PathType Container)) {
        throw 'The app-data location is a file instead of a folder. Nothing was removed.'
    }
    return $resolved
}

function Assert-Contained([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($script:resolvedRoot + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Data cleanup was cancelled because a selected file is outside the app-data folder.'
    }
    Assert-PlainPath $resolved
}

function Assert-ClosedProcessMetadata([object[]]$Processes,[string]$Root) {
    # Side-effect-free policy over verified native metadata; original app and
    # helper matching is retained. An unreadable relevant command line refuses
    # cleanup rather than treating missing metadata as evidence of safety.
    if ([string]::IsNullOrWhiteSpace($Root)) { throw 'The selected app cache could not be verified.' }
    foreach ($process in $Processes) {
        if (!$process -or [string]::IsNullOrWhiteSpace($process.Name)) { throw 'Running process metadata could not be verified. Personal data is kept.' }
        if ($process.Name -match '^(SayWhat|LocalTalk|VRChatIncomingTranslator|IncomingTranslator|FoxTrans|FoxTrans[.](LocalHost|Cli|Desktop))[.]exe$') {
            throw 'Close SayWhat? and its translation tools before continuing. No running program was stopped.'
        }
        if ($process.Name -match '^(powershell|pwsh|wsl)[.]exe$') {
            if ([string]::IsNullOrWhiteSpace($process.CommandLine)) { throw 'A relevant running process could not be verified. Personal data is kept.' }
            if ($process.CommandLine -match 'run-voxtral[.]sh|FoxTrans-VRChatMuteSync[.]ps1|FoxTrans-OscSentenceProxy[.]ps1') {
                throw 'A translation helper is still running. Stop translation and close the app before continuing.'
            }
        }
        if ($process.Name -in @('llama-server.exe','nemo-speech.exe')) {
            if ([string]::IsNullOrWhiteSpace($process.CommandLine) -or [string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
                throw 'A relevant running model could not be verified. Personal data is kept.'
            }
            if ($process.ExecutablePath.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase) -or
                $process.CommandLine.IndexOf($Root+'\',[StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw 'A model in the selected app cache is still running. Stop translation before clearing its files.'
            }
        }
    }
}

function Assert-SpeechListenersStopped([int[]]$Ports) {
    if ($Ports | Where-Object { $_ -in @(8080,8081) }) {
        throw 'Speech translation is still running. Stop its server before clearing data. No server was stopped by setup.'
    }
}

function Assert-DataChoiceTokenPolicy([bool]$HasSelectedChanges,[bool]$IsElevated,[bool]$IsAdministrator) {
    # Data cleanup must run only with the original user's restricted token.
    # Elevated uninstall/administrative metadata operations may keep data, but
    # must never traverse or alter user-writable paths with privileged rights.
    if ($HasSelectedChanges -and ($IsElevated -or $IsAdministrator)) {
        throw 'Personal data cannot be cleared with administrator permissions. Your settings, saved people and models are kept. Start setup normally to change personal data.'
    }
}

function Assert-SelectedDataToken([bool]$HasSelectedChanges) {
    if (!$HasSelectedChanges) { return }
    Initialize-ProcessMetadata
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $admin=[Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Assert-DataChoiceTokenPolicy $true ([SayWhatInstallerProcessMetadata]::CurrentTokenIsElevated()) $admin
    } finally { $identity.Dispose() }
}

function Assert-AppClosed {
    # Retain the pre-rebrand mutex: an older version must not write while cleanup runs.
    $created = $false
    $script:mutex = [Threading.Mutex]::new($true,'Local\LocalTalkDesktop',[ref]$created)
    $script:ownsMutex = $created
    if (!$created) { throw 'Close SayWhat? (including its tray icon) before updating, uninstalling or clearing data.' }
    Initialize-ProcessMetadata
    Assert-ClosedProcessMetadata @([SayWhatInstallerProcessMetadata]::RelevantProcesses()) $script:resolvedRoot
    # Read OS metadata only; never connect to an endpoint or start a dormant WSL distro.
    # Ports 1236 and unrelated GPU applications are intentionally not blanket blockers.
    if ($ResetPreferences -or $ClearHistory -or $DeleteCachedModels) {
        Assert-SpeechListenersStopped @([SayWhatInstallerProcessMetadata]::TcpListenerPorts())
    }
}

function Add-SelectedFile([string]$RelativeName) {
    $path = [IO.Path]::GetFullPath((Join-Path $script:resolvedRoot $RelativeName))
    Assert-Contained $path
    if (!(Test-Path -LiteralPath $path)) { return }
    $entry = Get-Item -LiteralPath $path -Force
    if ($entry.PSIsContainer) { throw 'A selected data file is a folder. Nothing was removed; check the app-data location.' }
    if ($script:seen.Add($path)) { $script:plan.Add([pscustomobject]@{ Path=$path; Relative=$RelativeName; Length=$entry.Length; Rewrite=$false }) }
}

function Write-Receipt([bool]$Success,[string]$Message,[string]$Details = '',[string]$BackupPath = '') {
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    if ($receipt.StartsWith($script:resolvedRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $receipt -eq $script:resolvedRoot) {
        throw 'The cleanup receipt must be outside the app-data folder.'
    }
    Assert-PlainPath $receipt
    $parent = [IO.Path]::GetDirectoryName($receipt)
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'The cleanup receipt folder is missing.' }
    if ((Test-Path -LiteralPath $receipt) -and !(Test-Path -LiteralPath $receipt -PathType Leaf)) { throw 'The cleanup receipt path is not a file.' }
    $safeMessage = $Message -replace '[\r\n]',' '
    $safeDetails = $Details -replace '[\r\n]',' '
    $safeBackup = $BackupPath -replace '[\r\n]',' '
    $successValue = if ($Success) { '1' } else { '0' }
    $text = "[Result]`r`nSuccess=$successValue`r`nMessage=$safeMessage`r`nDetails=$safeDetails`r`nBackupPath=$safeBackup`r`nRemovedFiles=$script:deletedCount`r`nReceiptNonce=$ReceiptNonce`r`n"
    Write-ReceiptText $text
}

function Remove-EmptyTransactionFolders {
    # Nonrecursive, exact empty directories only; no broad recursive delete commands.
    if (!$script:quarantine -or !(Test-Path -LiteralPath $script:quarantine)) { return }
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $script:moved) {
        $parent = [IO.Path]::GetDirectoryName($item.Destination)
        while ($parent -and $parent.StartsWith($script:quarantine+'\',[StringComparison]::OrdinalIgnoreCase)) {
            [void]$directories.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    foreach ($directory in @($directories | Sort-Object Length -Descending)) {
        Assert-Contained $directory
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
            [IO.Directory]::Delete($directory,$false)
        }
    }
    Assert-Contained $script:quarantine
    if (@(Get-ChildItem -LiteralPath $script:quarantine -Force).Count -eq 0) { [IO.Directory]::Delete($script:quarantine,$false) }
}

try {
    # This unconditional gate also applies to ValidateOnly and callers without
    # original-user switches. It precedes every selected-root probe or stage.
    Assert-SelectedDataToken ([bool]($ResetPreferences -or $ClearHistory -or $DeleteCachedModels))
    if ($PrepareProtectedReceipt) { Prepare-ProtectedReceipt; exit 0 }
    if ($RequireOriginalUserToken -and !$RequireProtectedReceipt) { throw 'A protected original-user receipt is required.' }
    if ($RequireProtectedReceipt) {
        [void](Assert-ProtectedReceipt)
        if (!$RequirePrivateUserRoot -and ($ResetPreferences -or $ClearHistory -or $DeleteCachedModels)) { throw 'Private-root verification is required for selected data changes.' }
    }
    if ($ResolveUserIdentity) { Resolve-UserIdentityReceipt }
    $resolvedRoot = Resolve-SafeRoot $DataRoot
    if ($RequirePrivateUserRoot -or ($ExpectedUserSid -and !$RequireProtectedReceipt)) {
        if (!$RequirePrivateUserRoot -or $ExpectedUserSid -notmatch '^S-1-5-[0-9-]+$') {
            throw 'A verified Windows account is required for private-data changes.'
        }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { if ($identity.User.Value -ne $ExpectedUserSid) { throw 'The Windows account changed. Personal data is kept.' } }
        finally { $identity.Dispose() }
        $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if (!$resolvedRoot.Equals((Join-Path $local 'SayWhat'),[StringComparison]::OrdinalIgnoreCase) -and
            !$resolvedRoot.Equals((Join-Path $local 'LocalTalk'),[StringComparison]::OrdinalIgnoreCase)) {
            throw 'The selected folder is outside this Windows account''s private app data. Personal data is kept.'
        }
        $user = Get-PrivateUserIdentity
        if (!$user.Supported -or !$resolvedRoot.Equals($user.Root,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'The selected folder does not match this Windows account''s private app data. Personal data is kept.'
        }
    }
    # Validate the receipt before touching any data, including receipt/data overlap.
    $receiptFull = [IO.Path]::GetFullPath($ReceiptPath)
    if ($receiptFull -eq $resolvedRoot -or $receiptFull.StartsWith($resolvedRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The cleanup receipt must be outside the app-data folder.'
    }
    Assert-PlainPath $receiptFull
    if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($receiptFull)) -PathType Container)) { throw 'The cleanup receipt folder is missing.' }
    # A write/access failure must happen before any reset or removal begins.
    Write-Receipt $false 'Checking selected data choices.'
    Assert-AppClosed
    $plan = New-Object 'System.Collections.Generic.List[object]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $preferenceNames = @('vrchat-translation-settings.json','config.fast-chinese.jsonc','config.sentence-mode.jsonc')
    # setup-progress.json and provisioning.json retain the installation's WSL ownership/registrations.
    $historyNames = @('incoming-ai-speaker-profiles.json','incoming-speaker-feedback.json','incoming-speaker-profiles.json',
        'foxtrans-local-host.log','foxtrans-mute-sync.log','foxtrans-osc-sentence.log','foxtrans-provider-errors.log',
        'incoming-translator.log','incoming-vr-overlay.log','saywhat.log','localtalk.log')
    if ($ResetPreferences) { foreach ($name in $preferenceNames) { Add-SelectedFile $name } }
    if ($ClearHistory) {
        foreach ($name in $historyNames) { Add-SelectedFile $name }
        # Personal pictures are app-owned GUID leaves, not imported originals.
        # Validate the directory and every child before staging any removal;
        # unknown names are retained, and links/subdirectories are refused.
        $photoRoot = Join-Path $resolvedRoot 'people-photos'
        Assert-Contained $photoRoot
        if (Test-Path -LiteralPath $photoRoot) {
            if (!(Test-Path -LiteralPath $photoRoot -PathType Container)) { throw 'The saved people photo folder is not a folder. Nothing was removed.' }
            $photoEntries = @(Get-ChildItem -LiteralPath $photoRoot -Force)
            foreach ($photo in $photoEntries) {
                Assert-Contained $photo.FullName
                if ($photo.PSIsContainer) { throw 'The saved people photo folder contains an unexpected subfolder. Nothing was removed.' }
            }
            foreach ($photo in $photoEntries) {
                if ($photo.Name -match '^[a-f0-9]{32}[.]png([.]tmp-[a-f0-9]{32})?$') { Add-SelectedFile ('people-photos\' + $photo.Name) }
            }
        }
    }
    if ((Test-Path -LiteralPath $resolvedRoot -PathType Container) -and ($ResetPreferences -or $ClearHistory)) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $resolvedRoot -Force)) {
            $name = $entry.Name
            $selected = ($ResetPreferences -and $name -match '^(vrchat-translation-settings[.]json|config[.](fast-chinese|sentence-mode)[.]jsonc)[.]tmp-[a-f0-9]{32}$') -or
                ($ClearHistory -and $name -match '^incoming-(ai-speaker-profiles|speaker-feedback|speaker-profiles)[.]json[.](tmp(-[a-f0-9]{32})?|backup-[0-9]{8}-[0-9]{6}-[0-9]{7}|before-named-feedback-v1[.]backup)$')
            if ($selected) { Add-SelectedFile $name }
        }
    }
    $modelCount = 0
    if ($DeleteCachedModels) {
        $modelRoot = Join-Path $resolvedRoot 'models'
        Assert-Contained $modelRoot
        if (Test-Path -LiteralPath $modelRoot) {
            if (!(Test-Path -LiteralPath $modelRoot -PathType Container)) { throw 'The model cache is not a folder. Nothing was removed.' }
            # Validate the WHOLE tree before modifying preferences or known model files.
            $queue = New-Object 'System.Collections.Generic.Queue[string]'
            $queue.Enqueue($modelRoot)
            while ($queue.Count -gt 0) {
                $folder = $queue.Dequeue(); Assert-Contained $folder
                foreach ($entry in @(Get-ChildItem -LiteralPath $folder -Force)) {
                    Assert-Contained $entry.FullName
                    if ($entry.PSIsContainer) { $queue.Enqueue($entry.FullName) }
                }
            }
        }
        $modelNames = @('models\Qwen_Qwen3-1.7B-Q4_K_M.gguf','models\Qwen3-4B-Q4_K_M.gguf',
            'models\Qwen3-8B-Q4_K_M.gguf','models\Qwen3-14B-Q4_K_M.gguf',
            'models\Hy-MT2-1.8B-Q4_K_M.gguf','models\Hy-MT2-7B-Q4_K_M.gguf',
            'models\translategemma-4b-it-Q4_K_M.gguf','models\LFM2-350M-ENJP-MT-Q4_K_M.gguf',
            'models\Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf',
            'models\own-speech\nemotron-3.5-asr-streaming-0.6b.q8_0.gguf',
            'models\speaker-id\3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx',
            'models\speaker-id\3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx',
            'models\separation\mossformer2-ss-16k.int8.onnx')
        foreach ($name in $modelNames) {
            $before = $plan.Count; Add-SelectedFile $name; $modelCount += $plan.Count - $before
            $parent = [IO.Path]::GetDirectoryName((Join-Path $resolvedRoot $name))
            if (Test-Path -LiteralPath $parent -PathType Container) {
                $partialPattern = '^' + [Regex]::Escape([IO.Path]::GetFileName($name)) + '[.](part-[a-f0-9]{32}|partial-[a-f0-9]{32}|download)$'
                foreach ($entry in @(Get-ChildItem -LiteralPath $parent -Force)) {
                    if ($entry.Name -match $partialPattern) {
                        $relative = $entry.FullName.Substring($resolvedRoot.Length+1)
                        $before = $plan.Count; Add-SelectedFile $relative; $modelCount += $plan.Count - $before
                    }
                }
            }
        }
    }
    $settingsPath = Join-Path $resolvedRoot 'vrchat-translation-settings.json'
    $replacementSettings = $null
    if ($DeleteCachedModels -and $modelCount -gt 0 -and !$ResetPreferences -and (Test-Path -LiteralPath $settingsPath)) {
        Assert-Contained $settingsPath
        if (!(Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw 'Saved settings are not a file. No model was removed.' }
        $document = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
        if (!$document -or $document -is [Array] -or $document -is [string] -or $document -is [ValueType]) {
            throw 'Saved settings could not be checked. Repair them before removing model files.'
        }
        $document | Add-Member -NotePropertyName FirstRunSetupCompleted -NotePropertyValue $false -Force
        $replacementSettings = $document | ConvertTo-Json -Depth 100
        Add-SelectedFile 'vrchat-translation-settings.json'
        $plan[$plan.Count-1].Rewrite = $true
    }
    foreach ($item in $plan) {
        # Locked/read-only files fail before the first move, leaving all selected data intact.
        if ((Get-Item -LiteralPath $item.Path -Force).Attributes -band [IO.FileAttributes]::ReadOnly) {
            throw 'A selected data file is read-only. Nothing was removed.'
        }
        $probe = [IO.File]::Open($item.Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $probe.Dispose()
    }
    if ($ValidateOnly) {
        Write-Receipt $true 'The selected data choices are ready. No data was changed.' ("Selected files: " + $plan.Count)
        exit 0
    }
    if ($plan.Count -eq 0) {
        Write-Receipt $true 'Your existing data was kept. No selected cached file needed removal.'
        exit 0
    }
    $quarantine = Join-Path $resolvedRoot ('.installer-data-change-' + [Guid]::NewGuid().ToString('N'))
    Assert-Contained $quarantine
    if (Test-Path -LiteralPath $quarantine) { throw 'The data cleanup transaction already exists. Nothing was removed.' }
    [IO.Directory]::CreateDirectory($quarantine) | Out-Null
    foreach ($item in $plan) {
        Assert-Contained $item.Path
        $destination = Join-Path $quarantine $item.Relative
        Assert-Contained $destination
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Move($item.Path,$destination)
        $moved.Add([pscustomobject]@{ Source=$item.Path; Destination=$destination; Rewrite=$item.Rewrite })
    }
    if ($replacementSettings) {
        Assert-Contained $settingsPath
        # CreateNew never overwrites a file created unexpectedly after preflight.
        $stream = [IO.File]::Open($settingsPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($replacementSettings); $stream.Write($bytes,0,$bytes.Length) }
        finally { $stream.Dispose() }
        $settingsRewritten = $true
    }
    $commitStarted = $true
    foreach ($item in $moved) {
        Assert-Contained $item.Destination
        [IO.File]::Delete($item.Destination)
        if (!$item.Rewrite) { $deletedCount++ }
    }
    Remove-EmptyTransactionFolders
    $details = 'Only selected, recognized app files were removed. Imported model files outside the app cache, speech support and runtime registrations were kept.'
    if ($settingsRewritten) { $details += ' Model setup will be offered again; other preferences were kept.' }
    Write-Receipt $true 'Your selected data choices were applied.' $details
    exit 0
}
catch {
    if ($PrepareProtectedReceipt) { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
    $originalFailure = $_.Exception.Message
    $rollbackFailure = $null
    if (!$commitStarted) {
        try {
            if ($settingsRewritten -and (Test-Path -LiteralPath $settingsPath)) { Assert-Contained $settingsPath; [IO.File]::Delete($settingsPath) }
            for ($index = $moved.Count-1; $index -ge 0; $index--) {
                $item = $moved[$index]; Assert-Contained $item.Source; Assert-Contained $item.Destination
                if (Test-Path -LiteralPath $item.Source) { throw 'A replacement data file appeared during cleanup; it was not overwritten.' }
                if (Test-Path -LiteralPath $item.Destination) { [IO.File]::Move($item.Destination,$item.Source) }
            }
            Remove-EmptyTransactionFolders
        }
        catch { $rollbackFailure = $_.Exception.Message }
    }
    $message = if ($commitStarted) { 'Some selected data could not be removed. App installation or removal is complete, but data cleanup needs attention.' }
        elseif ($rollbackFailure) { 'Data cleanup did not finish. Some files are retained in the reported transaction folder.' }
        else { 'Data cleanup did not run. Your existing data was kept.' }
    $remaining = if ($quarantine -and (Test-Path -LiteralPath $quarantine)) { $quarantine } else { '' }
    try {
        # Invalid roots still receive a failure receipt if the independently selected temp path is safe.
        if (!(Get-Variable -Name resolvedRoot -ErrorAction SilentlyContinue)) { $resolvedRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd([char[]]@('\','/')) }
        Write-Receipt $false $message $originalFailure $remaining
    }
    catch { Write-Error -Message ('Data cleanup failed; its receipt could not be saved. ' + $originalFailure) -ErrorAction Continue }
    exit 1
}
finally {
    if ($mutex) { if ($ownsMutex) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}
