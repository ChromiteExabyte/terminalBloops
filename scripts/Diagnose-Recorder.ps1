#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UserSid,
    [Parameter(Mandatory = $true)][string]$DataDirectory
)

. (Join-Path $PSScriptRoot 'Recorder.Common.ps1')
$exitCode = 10
$resultDirectory = $null
$result = [ordered]@{
    success = $false; diagnosedUtc = [DateTime]::UtcNow.ToString('o'); message = ''; exitCode = 10
    metadataPresent = $false; ownership = $null; services = @(); channel = $null
    environment = [ordered]@{ TEMP = $env:TEMP; TMP = $env:TMP; resolvedTemporaryDirectory = [IO.Path]::GetTempPath() }
    nativeSetupError = ''; nativeSetupErrorReadable = ''; nativeRemovalError = ''; nativeRemovalErrorReadable = ''
}
try {
    Assert-Administrator
    $resultDirectory = Assert-UserDataDirectory $UserSid $DataDirectory
    # Only inspect an existing protected directory. Diagnostics never installs, repairs or reconfigures a recorder.
    if (Test-Path -LiteralPath $script:RecorderRoot) {
        New-PrivateDirectory (Join-Path $env:ProgramData 'TerminalBloops')
        New-PrivateDirectory $script:RecorderRoot
        $state = Read-RecorderState
        if ($state) {
            $backupCount = if ($null -ne $state.PSObject.Properties['recoveryBackups']) { @($state.recoveryBackups).Count } else { 0 }
            $result.metadataPresent = $true
            $result.ownership = [ordered]@{
                owned = [bool]$state.owned; installPending = [bool]$state.installPending
                ownerMatchesRequestedUser = $state.ownerSid -eq $UserSid
                createdUtc = $state.createdUtc; recoveryBackupCount = $backupCount
            }
        }
        foreach ($entry in @(
            @{ name = 'last-setup-native-error.txt'; property = 'nativeSetupError' },
            @{ name = 'last-removal-native-error.txt'; property = 'nativeRemovalError' }
        )) {
            $path = Join-Path $script:RecorderRoot $entry.name
            if (Test-Path -LiteralPath $path) {
                Assert-NoReparsePoint $path
                $diagnostic = [IO.File]::ReadAllText($path)
                if ($diagnostic.Length -gt 65536) { $diagnostic = $diagnostic.Substring(0, 65536) + "`r`n[truncated after 64 KiB]" }
                $result[$entry.property] = $diagnostic
                # Preserve the original text as well as an inspection-friendly view of nested mixed-encoding output.
                $result[$entry.property + 'Readable'] = $diagnostic.Replace([string][char]0, '').Trim([char]0xFEFF).Trim()
            }
        }
    }
    foreach ($name in @('EventLog', 'Sysmon', 'Sysmon64', 'SysmonDrv')) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        $registered = Test-Path -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $name)
        $result.services += [ordered]@{
            name = $name; registered = [bool]$registered; found = $null -ne $service
            status = if ($service) { $service.Status.ToString() } else { 'Not found' }
        }
    }
    $channel = Get-RecorderChannel
    if ($channel) {
        try {
            $result.channel = [ordered]@{
                exists = $true; enabled = $channel.IsEnabled; maximumSizeInBytes = $channel.MaximumSizeInBytes
                logMode = $channel.LogMode.ToString()
            }
        } finally { $channel.Dispose() }
    } else { $result.channel = [ordered]@{ exists = $false } }
    $result.success = $true
    $result.message = 'Recorder diagnostics collected. No recorder settings were changed.'
    $exitCode = 0
} catch { $result.message = $_.Exception.Message }
finally {
    $result.exitCode = $exitCode
    if ($resultDirectory) {
        try { Write-UserResult $resultDirectory 'recorder-diagnostics.json' $result }
        catch { Write-Warning 'Could not write recorder-diagnostics.json.' }
    }
}
Write-Output $result.message
exit $exitCode
