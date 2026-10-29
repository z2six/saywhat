#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$FixtureBase,[Parameter(Mandatory=$true)][string]$SourceHelper)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    $checks = New-Object 'System.Collections.Generic.List[string]'
    function Check([bool]$Condition,[string]$Name) { if (!$Condition) { throw $Name }; $checks.Add($Name) }
    $base = [IO.Path]::GetFullPath($FixtureBase).TrimEnd('\')
    $allowed = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\offline-lifecycle-fixtures')).TrimEnd('\')
    if (!$base.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $base -PathType Container)) { throw 'Identity tests require an existing unique synthetic fixture directory.' }
    # Import ONLY the two side-effect-free policy functions from the actual
    # reviewed helper, never its entry point, identity/process probes or cleanup.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($SourceHelper,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'The data helper has a syntax error.' }
    foreach ($name in @('Resolve-PrivateRootSelection','Assert-InteractiveOwners')) {
        $definition=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name },$false)
        if (!$definition) { throw 'A required reviewed policy function is missing.' }
        Invoke-Expression $definition.Extent.Text
    }
    $accountA=Join-Path $base 'identity-policy\account-A\AppData\Local'
    $accountB=Join-Path $base 'identity-policy\account-B\AppData\Local'
    [IO.Directory]::CreateDirectory($accountA) | Out-Null
    [IO.Directory]::CreateDirectory($accountB) | Out-Null
    $current=Join-Path $accountA 'SayWhat'; $legacy=Join-Path $accountA 'LocalTalk'
    $result=Resolve-PrivateRootSelection $accountA '' ''
    Check ($result.Supported -and $result.Root -eq $current -and !(Test-Path -LiteralPath $current)) 'fresh identity selects SayWhat without creating its data folder'
    [IO.Directory]::CreateDirectory($legacy) | Out-Null
    $result=Resolve-PrivateRootSelection $accountA '' ''
    Check ($result.Supported -and $result.Root -eq $legacy) 'existing legacy root is preserved without moving or merging'
    [IO.Directory]::CreateDirectory($current) | Out-Null
    $result=Resolve-PrivateRootSelection $accountA '' ''
    Check ($result.Supported -and $result.Root -eq $current) 'current root wins when both private folders exist'
    $result=Resolve-PrivateRootSelection $accountA $legacy (Join-Path $accountB 'SayWhat')
    Check ($result.Supported -and $result.Root -eq $legacy) 'current environment override precedes legacy override only within own private root'
    $result=Resolve-PrivateRootSelection $accountA '' $legacy
    Check ($result.Supported -and $result.Root -eq $legacy) 'legacy environment override remains compatible inside own private root'
    foreach ($foreign in @((Join-Path $accountB 'SayWhat'),(Join-Path $base 'ProgramData\SayWhat'),(Join-Path $accountA 'SayWhat-child'),(Join-Path $base 'custom\LocalTalk'))) {
        $result=Resolve-PrivateRootSelection $accountA $foreign ''
        Check (!$result.Supported) ('foreign/shared/custom root is never eligible: '+$foreign)
    }
    $sid='S-1-5-21-1-2-3-1001'; $other='S-1-5-21-1-2-3-1002'
    Assert-InteractiveOwners $sid @([pscustomobject]@{ReturnValue=0;Sid=$sid})
    Check $true 'same-account shell validates independently of installation scope or elevation'
    Assert-InteractiveOwners $sid @([pscustomobject]@{ReturnValue=0;Sid=$sid},[pscustomobject]@{ReturnValue=0;Sid=$sid})
    Check $true 'multiple same-account shells in the session remain supported'
    $cases=@(
        @{Name='missing interactive shell';Owners=@()},
        @{Name='alternate UAC credentials or elevated fallback';Owners=@([pscustomobject]@{ReturnValue=0;Sid=$other})},
        @{Name='unreadable shell owner';Owners=@([pscustomobject]@{ReturnValue=2;Sid=$sid})},
        @{Name='ambiguous mixed-account session';Owners=@([pscustomobject]@{ReturnValue=0;Sid=$sid},[pscustomobject]@{ReturnValue=0;Sid=$other})}
    )
    foreach ($case in $cases) {
        $refused=$false
        try { Assert-InteractiveOwners $sid $case.Owners } catch { $refused=$true }
        Check $refused ($case.Name+' fails closed')
    }
    [pscustomobject]@{Checks=$checks.ToArray()} | ConvertTo-Json -Depth 5
    exit 0
} catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
