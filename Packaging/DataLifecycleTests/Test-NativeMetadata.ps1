#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceHelper,[switch]$ReadOnlyProbe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
try {
    $checks=New-Object 'System.Collections.Generic.List[string]'
    function Check([bool]$Condition,[string]$Name) { if (!$Condition) { throw $Name }; $checks.Add($Name) }
    function Refuses([scriptblock]$Action) { try { & $Action; return $false } catch { return $true } }
    # Import reviewed function definitions only. Never execute helper startup,
    # receipt preparation, data selection, staging or cleanup.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($SourceHelper,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ('The data helper has a syntax error: '+$errors[0].Message) }
    foreach ($name in @('Initialize-ProcessMetadata','Assert-InteractiveOwners','Assert-ClosedProcessMetadata','Assert-SpeechListenersStopped','Assert-DataChoiceTokenPolicy','Assert-SelectedDataToken')) {
        $definition=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name },$false)
        if (!$definition) { throw ('Missing reviewed function: '+$name) }
        Invoke-Expression $definition.Extent.Text
    }
    $root='C:\Synthetic-SayWhat-Policy'
    function Info([string]$Name,[string]$Path='C:\Other\tool.exe',[string]$Command='unrelated --option value') {
        [pscustomobject]@{Name=$Name;ProcessId=12345;ExecutablePath=$Path;CommandLine=$Command}
    }
    # Baseline policy copied from the previous CIM consumer, not its metadata
    # transport. Complete verified metadata must retain identical decisions.
    function Original-Refuses([object[]]$Processes,[string]$Root) {
        foreach ($p in $Processes) {
            if ($p.Name -match '^(SayWhat|LocalTalk|VRChatIncomingTranslator|IncomingTranslator|FoxTrans|FoxTrans[.](LocalHost|Cli|Desktop))[.]exe$') { return $true }
            if ($p.Name -match '^(powershell|pwsh|wsl)[.]exe$' -and $p.CommandLine -and $p.CommandLine -match 'run-voxtral[.]sh|FoxTrans-VRChatMuteSync[.]ps1|FoxTrans-OscSentenceProxy[.]ps1') { return $true }
            if ($p.Name -eq 'llama-server.exe' -and (($p.ExecutablePath -and $p.ExecutablePath.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($p.CommandLine -and $p.CommandLine.IndexOf($Root+'\',[StringComparison]::OrdinalIgnoreCase) -ge 0))) { return $true }
        }
        return $false
    }
    $cases=New-Object 'System.Collections.Generic.List[object]'
    foreach ($name in @('SayWhat.exe','LocalTalk.exe','VRChatIncomingTranslator.exe','IncomingTranslator.exe','FoxTrans.exe','FoxTrans.LocalHost.exe','FoxTrans.Cli.exe','FoxTrans.Desktop.exe')) {
        $cases.Add(@{Name=$name;Processes=@((Info $name));Expected=$true})
    }
    foreach ($name in @('powershell.exe','pwsh.exe','wsl.exe')) {
        foreach ($script in @('run-voxtral.sh','FoxTrans-VRChatMuteSync.ps1','FoxTrans-OscSentenceProxy.ps1')) {
            $cases.Add(@{Name=($name+' '+$script);Processes=@((Info $name 'C:\Windows\tool.exe' ('tool -File '+$script)));Expected=$true})
        }
        $cases.Add(@{Name=($name+' unrelated');Processes=@((Info $name));Expected=$false})
    }
    $cases.Add(@{Name='model executable under selected cache';Processes=@((Info 'llama-server.exe' ($root+'\runtime\llama-server.exe')));Expected=$true})
    $cases.Add(@{Name='model argument under selected cache';Processes=@((Info 'llama-server.exe' 'C:\Other\llama-server.exe' ('--model "'+$root+'\models\one.gguf"')));Expected=$true})
    $cases.Add(@{Name='model under similarly named sibling';Processes=@((Info 'llama-server.exe' ($root+'-other\llama-server.exe') ('--model "'+$root+'-other\one.gguf"')));Expected=$false})
    $cases.Add(@{Name='unrelated model library';Processes=@((Info 'llama-server.exe'));Expected=$false})
    $cases.Add(@{Name='unrelated program';Processes=@((Info 'other.exe'));Expected=$false})
    $cases.Add(@{Name='empty snapshot';Processes=@();Expected=$false})
    foreach ($case in $cases) {
        $original=Original-Refuses $case.Processes $root
        $actual=Refuses { Assert-ClosedProcessMetadata $case.Processes $root }
        Check ($original -eq $case.Expected -and $actual -eq $original) ('unchanged process policy: '+$case.Name)
    }
    foreach ($name in @('llama-server.exe','nemo-speech.exe')) {
        Check (Refuses { Assert-ClosedProcessMetadata @((Info $name ($root+'\runtime\'+$name))) $root }) ($name+' selected-cache executable blocks')
        Check (Refuses { Assert-ClosedProcessMetadata @((Info $name 'C:\Other\server.exe' ('--model '+$root+'\models\one.gguf'))) $root }) ($name+' selected-cache model argument blocks')
        Check (!(Refuses { Assert-ClosedProcessMetadata @((Info $name ($root+'-other\'+$name) ('--model '+$root+'-other\one.gguf'))) $root })) ($name+' unrelated sibling remains allowed')
        Check (Refuses { Assert-ClosedProcessMetadata @((Info $name 'C:\Other\server.exe' '')) $root }) ($name+' unreadable command line fails closed')
        Check (Refuses { Assert-ClosedProcessMetadata @((Info $name '' '--model unrelated.gguf')) $root }) ($name+' unreadable image path fails closed')
    }
    foreach ($name in @('powershell.exe','pwsh.exe','wsl.exe')) {
        Check (Refuses { Assert-ClosedProcessMetadata @((Info $name 'C:\Windows\tool.exe' '')) $root }) ($name+' unreadable command line fails closed')
    }
    Check (Refuses { Assert-ClosedProcessMetadata @([pscustomobject]@{Name=''}) $root }) 'invalid process metadata fails closed'
    foreach ($ports in @(@(),@(1236),@(80,443,1236),@(8080),@(8081),@(1236,8081))) {
        $expected=@($ports | Where-Object { $_ -in @(8080,8081) }).Count -gt 0
        Check ((Refuses { Assert-SpeechListenersStopped $ports }) -eq $expected) ('unchanged TCP listener policy: '+($ports -join ','))
    }
    $sid='S-1-5-21-1-2-3-1001'
    Assert-InteractiveOwners $sid @([pscustomobject]@{ReturnValue=0;Sid=$sid})
    Check $true 'original interactive owner policy accepts same SID'
    foreach ($owners in @(@(),@([pscustomobject]@{ReturnValue=0;Sid='S-1-5-21-1-2-3-1002'}),@([pscustomobject]@{ReturnValue=2;Sid=$sid}))) {
        Check (Refuses { Assert-InteractiveOwners $sid $owners }) 'original interactive owner failures remain closed'
    }
    foreach ($selected in @($false,$true)) {
        foreach ($elevated in @($false,$true)) {
            foreach ($admin in @($false,$true)) {
                $expected=$selected -and ($elevated -or $admin)
                Check ((Refuses { Assert-DataChoiceTokenPolicy $selected $elevated $admin }) -eq $expected) ('selected-data token gate: selected='+$selected+', elevated='+$elevated+', admin='+$admin)
            }
        }
    }
    $mainTry=$ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] } | Select-Object -Last 1
    Check ($mainTry.Body.Statements[0].Extent.Text -match '^Assert-SelectedDataToken' -and $mainTry.Body.Statements[0].Extent.Text -match 'ResetPreferences -or \$ClearHistory -or \$DeleteCachedModels') 'all selected choices gate before root probes even without original-user flags or with ValidateOnly'
    $probe=$null
    if ($ReadOnlyProbe) {
        Initialize-ProcessMetadata
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        try { $currentSid=$identity.User.Value; $currentAdmin=[Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
        $currentElevated=[SayWhatInstallerProcessMetadata]::CurrentTokenIsElevated()
        Check ((Refuses { Assert-SelectedDataToken $true }) -eq ($currentElevated -or $currentAdmin)) 'actual token gate agrees with native elevation and enabled administrator role without touching data'
        Assert-SelectedDataToken $false
        Check $true 'no-choice metadata remains allowed under actual token'
        $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
        $owners=@([SayWhatInstallerProcessMetadata]::ShellOwners([uint32]$session))
        Assert-InteractiveOwners $currentSid $owners
        Check ($owners.Count -gt 0) 'native Explorer token owner matches current interactive SID without WMI'
        foreach ($owner in $owners) {
            Check ($owner.SessionId -eq $session -and $owner.CreationFileTime -gt 0 -and [IO.Path]::GetFileName($owner.ExecutablePath) -ieq 'explorer.exe') 'native Explorer session, held image and creation time are verified'
        }
        $self=[SayWhatInstallerProcessMetadata]::CurrentProcessForDiagnostics()
        Check ($self.ProcessId -eq $PID -and $self.SessionId -eq $session -and $self.CreationFileTime -gt 0 -and $self.CommandLine.IndexOf('Test-NativeMetadata.ps1',[StringComparison]::OrdinalIgnoreCase) -ge 0) 'native class-60 command line matches exact owned test process'
        $ipv4=$null; $ipv6=$null
        try {
            # Bind only self-owned loopback listeners on ephemeral fixture
            # ports. Never connect, bind app ports, or launch a speech server.
            $ipv4=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
            $ipv4.Start()
            if ([Net.Sockets.Socket]::OSSupportsIPv6) {
                $ipv6=[Net.Sockets.TcpListener]::new([Net.IPAddress]::IPv6Loopback,0)
                $ipv6.Server.DualMode=$false
                $ipv6.Start()
            }
            $ports=@([SayWhatInstallerProcessMetadata]::TcpListenerPorts())
            Check ($ports -contains $ipv4.LocalEndpoint.Port) 'native TCP metadata includes self-owned IPv4 ephemeral listener'
            if ($ipv6) { Check ($ports -contains $ipv6.LocalEndpoint.Port) 'native TCP metadata includes self-owned IPv6 ephemeral listener' }
            Check (@($ports | Where-Object { $_ -lt 1 -or $_ -gt 65535 }).Count -eq 0) 'native TCP metadata contains valid port numbers'
            $probe=[pscustomobject]@{InteractiveShells=$owners.Count;CurrentProcessVerified=$true;TcpListenerCount=$ports.Count;Ipv4FixtureObserved=$true;Ipv6FixtureObserved=($null -ne $ipv6);CurrentTokenElevated=$currentElevated;CurrentTokenAdministrator=$currentAdmin}
        } finally {
            if ($ipv6) { $ipv6.Stop() }
            if ($ipv4) { $ipv4.Stop() }
        }
    }
    [pscustomobject]@{Checks=$checks.ToArray();ReadOnlyProbe=$probe} | ConvertTo-Json -Depth 5
    exit 0
} catch { Write-Error -Message ($_.Exception.Message+' | '+$_.ScriptStackTrace) -ErrorAction Continue; exit 1 }
