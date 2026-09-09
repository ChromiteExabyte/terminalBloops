#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UserSid,
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [switch]$RecoverOnly
)

. (Join-Path $PSScriptRoot 'Recorder.Common.ps1')
$exitCode = 10
$resultDirectory = $null
$temporaryDirectory = $null
$executable = $null
$operationMode = if ($RecoverOnly) { 'recovery-only' } else { 'setup' }
$result = [ordered]@{ success = $false; mode = $operationMode; reusedExisting = $false; ownsInstallation = $false; message = ''; exitCode = 10 }
try {
    Assert-Administrator
    $resultDirectory = Assert-UserDataDirectory $UserSid $DataDirectory
    Initialize-RecorderDirectory
    $script:NativeFailureLogPath = Join-Path $script:RecorderRoot 'last-setup-native-error.txt'
    $state = Read-RecorderState
    if ($null -eq $state) {
        $state = [pscustomobject]@{
            productId = $script:ProductId; owned = $false; installPending = $false
            serviceName = ''; serviceImage = ''; binarySha256 = ''; configurationSha256 = ''
            ownerSid = ''; addedReaderSids = @(); createdUtc = [DateTime]::UtcNow.ToString('o')
        }
    }
    $channel = Get-RecorderChannel
    $hasChannel = $null -ne $channel
    if ($channel) { $channel.Dispose() }
    $existingService = Get-Service -Name Sysmon, Sysmon64 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hasChannel -or $null -ne $existingService) {
        if ($RecoverOnly) { throw 'Recovery-only mode refuses to change an existing Sysmon service or event channel.' }
        $result.reusedExisting = $true
        if (-not $hasChannel) { throw 'An existing Sysmon installation has no readable event channel. Its installation and configuration were preserved.' }
        if ($existingService -and $existingService.Status -ne 'Running') {
            throw 'The existing Sysmon service is stopped. Its configuration was preserved; ask its administrator to restore it.'
        }
        Save-RecorderState $state
    } else {
        if ($state.owned -or ($state.installPending -and $state.ownerSid -ne $UserSid)) {
            $remaining = @(Get-RecorderFootprints) -join ', '
            throw ('The interrupted recorder installation still has ownership or machine footprints to preserve. Remaining: ' + $remaining)
        }
        $exitCode = 20
        # Keep downloaded executable/configuration inside an administrator-only task temporary directory.
        $temporaryDirectory = Join-Path $script:RecorderRoot ('setup-' + [Guid]::NewGuid().ToString('N'))
        New-PrivateDirectory $temporaryDirectory
        $zip = Join-Path $temporaryDirectory 'Sysmon.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/Sysmon.zip' -OutFile $zip -UseBasicParsing
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.GetEntry('Sysmon64.exe')
            if ($null -eq $entry) { throw 'The Microsoft Sysmon download did not contain Sysmon64.exe.' }
            $executable = Join-Path $temporaryDirectory 'Sysmon64.exe'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $executable, $false)
        } finally { $archive.Dispose() }
        Assert-MicrosoftSignature $executable
        $exitCode = 30
        Repair-PendingInstallation $state $UserSid $executable
        if ($RecoverOnly) {
            $remaining = @(Get-RecorderFootprints)
            if ($remaining.Count -gt 0) { throw ('Recovery-only mode preserved unowned machine footprints: ' + ($remaining -join ', ')) }
        } else {
        # Permit Event Log's service identities to read verified installer resources during registration.
        # These transient ACLs add no write permission and do not change ownership metadata access.
        Grant-InstallerResourceRead $temporaryDirectory
        $configPath = Join-Path $temporaryDirectory 'process-events.xml'
        $configuration = New-ProcessRecorderConfiguration (Invoke-QuietProcess $executable @('-s'))
        [IO.File]::WriteAllText($configPath, $configuration, [Text.UTF8Encoding]::new($false))
        $state.installPending = $true
        $state.ownerSid = $UserSid
        Save-RecorderState $state
        # EULA acceptance occurs only on this explicitly requested setup operation.
        $null = Invoke-QuietProcess $executable @('-accepteula', '-i', $configPath) -HiddenConsole
        $service = Get-CimInstance Win32_Service -Filter "Name='Sysmon64'"
        if ($null -eq $service -or $service.State -ne 'Running') { throw 'Sysmon installation did not produce a running Sysmon64 service.' }
        $serviceImage = $service.PathName.Trim('"')
        if (-not [IO.File]::Exists($serviceImage)) { throw 'The installed Sysmon service executable could not be verified.' }
        Assert-MicrosoftSignature $serviceImage
        $state.owned = $true
        $state.serviceName = $service.Name
        $state.serviceImage = $serviceImage
        $state.binarySha256 = (Get-FileHash -LiteralPath $serviceImage -Algorithm SHA256).Hash
        Save-RecorderState $state
        $state.configurationSha256 = Get-TextHash (Invoke-QuietProcess $serviceImage @('-c'))
        $state.installPending = $false
        Save-RecorderState $state
        $channel = Get-RecorderChannel
        if ($null -eq $channel) { throw 'Sysmon installed but its event channel is unavailable. Retry setup after checking the service.' }
        try {
            $channel.MaximumSizeInBytes = 268435456
            $channel.LogMode = [Diagnostics.Eventing.Reader.EventLogMode]::Circular
            $channel.IsEnabled = $true
            $channel.SaveChanges()
        } finally { $channel.Dispose() }
        }
    }
    if ($RecoverOnly) {
        $result.message = 'Failed-installer recovery completed; verified remnants were preserved in the protected recovery folder when present. The recorder is NOT installed and recording is NOT active.'
    } else {
        $exitCode = 40
        Add-ReaderAccess $state $UserSid
        $result.ownsInstallation = [bool]$state.owned
        $result.message = if ($state.owned) { 'Recorder ready. Process starts and exits are recorded in the local Sysmon event channel.' }
            else { 'Existing Sysmon reused without changing its capture configuration or retention. Capture depends on its process-create and process-terminate rules.' }
    }
    $result.success = $true
    $exitCode = 0
} catch {
    # The status may include our installer's concise diagnostic, never arbitrary process history.
    $result.message = $_.Exception.Message
    # A clean installer failure is retryable. Preserve pending ownership if any installation remains.
    if (-not $RecoverOnly -and $exitCode -eq 30 -and $state -and $state.installPending -and -not $state.owned) {
        try {
            if ($executable -and (Test-Path -LiteralPath $executable)) {
                Repair-PendingInstallation $state $UserSid $executable
                if (-not $state.installPending) { $result.message += ' The failed attempt''s verified remnants were safely backed up; the recorder is not installed.' }
            } else { $null = Reset-CleanPendingInstallation $state $UserSid }
        } catch { $result.message += ' Check protected ownership metadata before retrying.' }
    }
} finally {
    $result.exitCode = $exitCode
    if ($resultDirectory) {
        try { Write-UserResult $resultDirectory 'setup-result.json' $result }
        catch { Write-Warning 'Could not write setup-result.json. Inspect the recorder helper exit code.' }
    }
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        $resolved = [IO.Path]::GetFullPath($temporaryDirectory)
        $expectedParent = [IO.Path]::GetFullPath($script:RecorderRoot).TrimEnd('\') + '\'
        if ($resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolved) -match '^setup-[0-9a-f]{32}$') {
            Assert-NoReparsePoint $resolved
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
Write-Output $result.message
exit $exitCode
