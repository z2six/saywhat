[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $OutputPath)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'z2six/saywhat' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This explicit inference fixture runs only on a GitHub-hosted Windows runner.'
}

# Windows PowerShell's System.Speech uses an installed Windows voice. This
# original fixture writes to a file; it never opens a recording/output device.
Add-Type -AssemblyName System.Speech
$voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $english = @($voice.GetInstalledVoices() | Where-Object {
        $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'en'
    })
    if ($english.Count -eq 0) { throw 'No installed English Windows voice is available for this fixture.' }
    $voice.SelectVoice($english[0].VoiceInfo.Name)
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo -ArgumentList @(16000,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono)
    $voice.SetOutputToWaveFile($OutputPath, $format)
    $voice.Speak('Today I am testing a local speech recognition program. The bright red umbrella is beside the door. We can walk to the small shop after lunch and buy some fresh fruit.')
    $voice.SetOutputToNull()
}
finally { $voice.Dispose() }
