#Requires -Version 5.1
# This probe is invoked only with a verified limited shell token. It opens handles
# to test access; it never changes an ACL, deletes a file or follows a data link.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [Parameter(Mandatory=$true)][string]$ProtectedLeaf
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ProtectedLeaf -notmatch '^is-[a-f0-9]{10}[.]tmp$') { throw 'Unexpected fixture leaf.' }
$ProtectedDirectory = Join-Path $FixtureRoot $ProtectedLeaf
$Helper = Join-Path $ProtectedDirectory 'manage-install-data.ps1'
$IdentityReceipt = Join-Path $ProtectedDirectory 'saywhat-data-identity.ini'
$ActionReceipt = Join-Path $ProtectedDirectory 'saywhat-data-actions.ini'
$Report = Join-Path $FixtureRoot 'access-probe.json'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ReceiptAccessProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    public static int OpenError(string path, uint access, bool directory) {
        using (var file=CreateFileW(path,access,7,IntPtr.Zero,3,
            directory ? 0x02000000u : 0u,IntPtr.Zero)) {
            return file.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
    }
}
'@
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $result = [ordered]@{
        UserSid = $identity.User.Value
        Account = $identity.Name
        IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        ParentRead = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,1,$true)
        ParentAddFile = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,2,$true)
        ParentAddDirectory = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,4,$true)
        ParentDeleteChild = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,64,$true)
        ParentDelete = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,65536,$true)
        ParentWriteDac = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,262144,$true)
        ParentWriteOwner = [ReceiptAccessProbe]::OpenError($ProtectedDirectory,524288,$true)
        HelperRead = [ReceiptAccessProbe]::OpenError($Helper,1,$false)
        HelperWrite = [ReceiptAccessProbe]::OpenError($Helper,2,$false)
        HelperDelete = [ReceiptAccessProbe]::OpenError($Helper,65536,$false)
        HelperWriteDac = [ReceiptAccessProbe]::OpenError($Helper,262144,$false)
        HelperWriteOwner = [ReceiptAccessProbe]::OpenError($Helper,524288,$false)
        IdentityRead = [ReceiptAccessProbe]::OpenError($IdentityReceipt,1,$false)
        IdentityWrite = [ReceiptAccessProbe]::OpenError($IdentityReceipt,2,$false)
        IdentityDelete = [ReceiptAccessProbe]::OpenError($IdentityReceipt,65536,$false)
        IdentityWriteDac = [ReceiptAccessProbe]::OpenError($IdentityReceipt,262144,$false)
        IdentityWriteOwner = [ReceiptAccessProbe]::OpenError($IdentityReceipt,524288,$false)
        ActionsRead = [ReceiptAccessProbe]::OpenError($ActionReceipt,1,$false)
        ActionsWrite = [ReceiptAccessProbe]::OpenError($ActionReceipt,2,$false)
        ActionsDelete = [ReceiptAccessProbe]::OpenError($ActionReceipt,65536,$false)
        ActionsWriteDac = [ReceiptAccessProbe]::OpenError($ActionReceipt,262144,$false)
        ActionsWriteOwner = [ReceiptAccessProbe]::OpenError($ActionReceipt,524288,$false)
        IdentityOpenReadWrite = $false
        ActionsOpenReadWrite = $false
        OldWriteDenied = $false
        OldWriteException = ''
    }
    foreach ($entry in @(@{Path=$IdentityReceipt;Key='IdentityOpenReadWrite'},@{Path=$ActionReceipt;Key='ActionsOpenReadWrite'})) {
        $stream = [IO.File]::Open($entry.Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
        $stream.Dispose()
        $result[$entry.Key] = $true
    }
    # This is the original WriteAllText/Create behavior in Inno's protected temp.
    # If the ACL is incorrect the tiny unexpected sibling is left for inspection.
    try { [IO.File]::WriteAllText((Join-Path $ProtectedDirectory 'old-unprepared-receipt.ini'),'synthetic receipt',[Text.Encoding]::Unicode) }
    catch {
        $errorObject = $_.Exception
        while ($errorObject.InnerException) { $errorObject = $errorObject.InnerException }
        $result.OldWriteDenied = $errorObject -is [UnauthorizedAccessException]
        $result.OldWriteException = $errorObject.GetType().FullName
    }
    [IO.File]::WriteAllText($Report,($result | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    exit 0
}
finally { $identity.Dispose() }
