#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UserSid,
    [Parameter(Mandatory = $true)][string]$DataDirectory
)

. (Join-Path $PSScriptRoot 'Recorder.Common.ps1')
$exitCode = 10
$resultDirectory = $null
$result = [ordered]@{ success = $false; removedOwnedInstallation = $false; message = ''; exitCode = 10 }
try {
    Assert-Administrator
    $resultDirectory = Assert-UserDataDirectory $UserSid $DataDirectory
    Initialize-RecorderDirectory
    $script:NativeFailureLogPath = Join-Path $script:RecorderRoot 'last-removal-native-error.txt'
    $state = Read-RecorderState
    if ($null -eq $state) { throw 'No protected Terminal Bloops recorder ownership metadata exists. Nothing was removed.' }
    $exitCode = 40
    if ($state.owned -and $state.ownerSid -eq $UserSid) {
        $otherReaders = @($state.addedReaderSids | Where-Object { $_ -ne $UserSid })
        if ($otherReaders.Count -gt 0) { throw 'Other Terminal Bloops users still use this recorder. Remove their reader grants before uninstalling it.' }
        $service = Get-CimInstance Win32_Service -Filter "Name='Sysmon64'"
        if ($null -eq $service -or $service.PathName.Trim('"') -ne $state.serviceImage -or $state.serviceName -ne 'Sysmon64') {
            throw 'The installed service no longer matches the owned recorder. Nothing was uninstalled.'
        }
        Assert-MicrosoftSignature $state.serviceImage
        if ((Get-FileHash -LiteralPath $state.serviceImage -Algorithm SHA256).Hash -ne $state.binarySha256) {
            throw 'Sysmon was replaced or updated since installation. Nothing was uninstalled; review it with its administrator.'
        }
        if ([string]::IsNullOrWhiteSpace($state.configurationSha256) -or
            (Get-TextHash (Invoke-QuietProcess $state.serviceImage @('-c'))) -ne $state.configurationSha256) {
            throw 'Sysmon configuration changed since installation. Nothing was uninstalled; review it with its administrator.'
        }
        Remove-ReaderAccess $state $UserSid
        $exitCode = 30
        $null = Invoke-QuietProcess $state.serviceImage @('-u') -HiddenConsole
        if (Get-Service -Name Sysmon64 -ErrorAction SilentlyContinue) { throw 'Sysmon removal did not finish. Local history and ownership metadata were retained.' }
        $state.owned = $false
        $state.installPending = $false
        Save-RecorderState $state
        $result.removedOwnedInstallation = $true
        $result.message = 'The Sysmon installation owned by Terminal Bloops was removed. Local history was retained.'
    } else {
        Remove-ReaderAccess $state $UserSid
        $result.message = 'The reader grant added for this user was removed. The shared Sysmon installation and local history were preserved.'
    }
    $result.success = $true
    $exitCode = 0
} catch { $result.message = $_.Exception.Message }
finally {
    $result.exitCode = $exitCode
    if ($resultDirectory) {
        try { Write-UserResult $resultDirectory 'remove-result.json' $result }
        catch { Write-Warning 'Could not write remove-result.json. Inspect the recorder helper exit code.' }
    }
}
Write-Output $result.message
exit $exitCode
