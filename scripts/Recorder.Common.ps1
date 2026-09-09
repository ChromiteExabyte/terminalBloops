# Shared implementation for elevated setup/removal. Never dot-source from a writable download.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ChannelName = 'Microsoft-Windows-Sysmon/Operational'
$script:ProductId = 'TerminalBloops.Recorder.v1'
$script:RecorderRoot = Join-Path $env:ProgramData 'TerminalBloops\Recorder'
$script:StatePath = Join-Path $script:RecorderRoot 'installation.json'
$script:NativeFailureLogPath = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This helper requires elevation. Use Set up recorder in Terminal Bloops, or run Windows PowerShell as administrator.'
    }
}

function Assert-NoReparsePoint([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            $item = Get-Item -LiteralPath $itemPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Recorder paths must not contain junctions or symbolic links.'
            }
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}

function Assert-UserDataDirectory([string]$Sid, [string]$Path) {
    $null = [Security.Principal.SecurityIdentifier]::new($Sid)
    $profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $Sid
    $profile = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath)
    $expected = [IO.Path]::GetFullPath((Join-Path $profile 'AppData\Local\TerminalBloops')).TrimEnd('\')
    $actual = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DataDirectory must be this user SID''s AppData\Local\TerminalBloops directory.'
    }
    Assert-NoReparsePoint $actual
    return $actual
}

function New-PrivateDirectory([string]$Path) {
    Assert-NoReparsePoint $Path
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    if (Test-Path -LiteralPath $Path) {
        $existing = Get-Acl -LiteralPath $Path
        $owner = $existing.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @($administrators.Value, $system.Value)) {
            throw 'The recorder metadata directory is not owned by Administrators or SYSTEM. Setup stopped without trusting it.'
        }
        foreach ($rule in $existing.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            $unsafeRights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
                [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                [Security.AccessControl.FileSystemRights]::TakeOwnership
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @($administrators.Value, $system.Value) -and
                ($rule.FileSystemRights -band $unsafeRights) -ne 0) {
                throw 'The recorder metadata directory permits non-administrator writes. Setup stopped without trusting it.'
            }
        }
        return
    }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($administrators, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    # This overload creates the directory with its restrictive ACL atomically on Windows PowerShell 5.1.
    $directory = [IO.DirectoryInfo]::new($Path)
    $directory.Create($acl)
}

function Initialize-RecorderDirectory {
    if ($PSVersionTable.PSEdition -ne 'Desktop') {
        throw 'Run this elevated helper with Windows PowerShell 5.1 (powershell.exe), not pwsh.exe.'
    }
    New-PrivateDirectory (Join-Path $env:ProgramData 'TerminalBloops')
    New-PrivateDirectory $script:RecorderRoot
}

function Read-RecorderState {
    if (-not (Test-Path -LiteralPath $script:StatePath)) { return $null }
    Assert-NoReparsePoint $script:StatePath
    $state = Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
    if ($state.productId -ne $script:ProductId) { throw 'Unrecognized recorder ownership metadata.' }
    return $state
}

function Save-RecorderState($State) {
    Assert-NoReparsePoint $script:StatePath
    $temporaryState = Join-Path $script:RecorderRoot ('state-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporaryState, ($State | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $script:StatePath) { [IO.File]::Replace($temporaryState, $script:StatePath, [NullString]::Value) }
    else { [IO.File]::Move($temporaryState, $script:StatePath) }
}

function Write-UserResult([string]$Directory, [string]$FileName, $Result) {
    Assert-NoReparsePoint $Directory
    $null = New-Item -ItemType Directory -Path $Directory -Force
    $path = Join-Path $Directory $FileName
    Assert-NoReparsePoint $path
    $Result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
}

function Assert-MicrosoftSignature([string]$Path) {
    Assert-NoReparsePoint $Path
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)' -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=(Microsoft Corporation|Microsoft Windows Publisher)(,|$)') {
        throw 'Sysmon did not pass the Microsoft Authenticode signature check. No unverified binary will run.'
    }
}

function Grant-InstallerResourceRead([string]$Directory) {
    $resolved = [IO.Path]::GetFullPath($Directory)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), [IO.Path]::GetFullPath($script:RecorderRoot), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^setup-[0-9a-f]{32}$') {
        throw 'Event Log resource access is limited to this setup attempt''s temporary directory.'
    }
    Assert-NoReparsePoint $resolved
    $eventLogSid = ([Security.Principal.NTAccount]::new('NT SERVICE', 'EventLog')).Translate([Security.Principal.SecurityIdentifier])
    $localServiceSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-19')
    $acl = Get-Acl -LiteralPath $resolved
    foreach ($sid in @($eventLogSid, $localServiceSid)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $resolved -AclObject $acl
    # Existing extracted files must be readable too. Subsequent config files inherit the same read-only grant.
    foreach ($file in Get-ChildItem -LiteralPath $resolved -File -Force -ErrorAction Stop) {
        Assert-NoReparsePoint $file.FullName
        $fileAcl = Get-Acl -LiteralPath $file.FullName
        foreach ($sid in @($eventLogSid, $localServiceSid)) {
            $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'ReadAndExecute', 'Allow'))
        }
        Set-Acl -LiteralPath $file.FullName -AclObject $fileAcl
    }
}

function Convert-NativeBytesToText([byte[]]$Bytes) {
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return '' }
    if ($Bytes.Length -ge 4 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE -and $Bytes[2] -eq 0 -and $Bytes[3] -eq 0) {
        return [Text.Encoding]::UTF32.GetString($Bytes, 4, $Bytes.Length - 4)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) {
        return [Text.Encoding]::Unicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) {
        return [Text.Encoding]::BigEndianUnicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    # Sysinternals may emit UTF-16 without a BOM when redirected. Infer only the
    # characteristic alternating NUL pattern; never decode then discard NULs.
    $sampleLength = [Math]::Min($Bytes.Length, 512)
    $evenNuls = 0; $oddNuls = 0
    for ($i = 0; $i -lt $sampleLength; $i++) {
        if ($Bytes[$i] -eq 0) {
            if (($i % 2) -eq 0) { $evenNuls++ } else { $oddNuls++ }
        }
    }
    if (($Bytes.Length % 2) -eq 0 -and $sampleLength -ge 4) {
        if ($oddNuls -gt ($sampleLength / 8) -and $evenNuls -lt ($sampleLength / 16)) { return [Text.Encoding]::Unicode.GetString($Bytes) }
        if ($evenNuls -gt ($sampleLength / 8) -and $oddNuls -lt ($sampleLength / 16)) { return [Text.Encoding]::BigEndianUnicode.GetString($Bytes) }
    }
    try { return [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
    catch [Text.DecoderFallbackException] { return [Text.Encoding]::Default.GetString($Bytes) }
}

function Invoke-QuietProcess([string]$FilePath, [string[]]$ArgumentList, [int]$TimeoutSeconds = 120, [switch]$HiddenConsole, [switch]$ShellLaunch) {
    $quoted = foreach ($argument in $ArgumentList) {
        if ($argument.Contains('"') -or $argument.Contains("`r") -or $argument.Contains("`n")) { throw 'Invalid native helper argument.' }
        if ($argument.Length -eq 0 -or $argument -match '\s') { '"' + [regex]::Replace($argument, '(\\+)$', '$1$1') + '"' }
        else { $argument }
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.Arguments = $quoted -join ' '
    $info.UseShellExecute = $ShellLaunch.IsPresent
    $info.CreateNoWindow = -not ($HiddenConsole -or $ShellLaunch)
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($FilePath))
    $info.RedirectStandardOutput = -not $ShellLaunch
    $info.RedirectStandardError = -not $ShellLaunch
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $stdoutBytes = [IO.MemoryStream]::new()
    $stderrBytes = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { throw 'The recorder helper could not start.' }
        if ($ShellLaunch) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill()
                throw 'The recorder helper timed out in hidden shell-launch mode. Check the Sysmon service before retrying.'
            }
            if ($process.ExitCode -ne 0) {
                throw ('The recorder helper returned exit code {0} in hidden shell-launch mode without redirected streams. Earlier native diagnostics were preserved.' -f $process.ExitCode)
            }
            return ''
        }
        $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stdoutBytes)
        $stderr = $process.StandardError.BaseStream.CopyToAsync($stderrBytes)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw 'The recorder helper timed out. Check the Sysmon service before retrying.'
        }
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        $output = Convert-NativeBytesToText $stdoutBytes.ToArray()
        $errorOutput = Convert-NativeBytesToText $stderrBytes.ToArray()
        if ($process.ExitCode -ne 0) {
            $diagnostic = ($output + $errorOutput).Trim([char]0xFEFF).Trim()
            $detail = @($diagnostic -split "`r?`n" | Where-Object { $_ -match '(?i)error|fail|invalid|denied|unsupported|cannot|could not' }) -join ' '
            if ($detail.Length -gt 800) { $detail = $detail.Substring(0, 800) }
            if ($script:NativeFailureLogPath) {
                Assert-NoReparsePoint $script:NativeFailureLogPath
                [IO.File]::WriteAllText($script:NativeFailureLogPath, $diagnostic, [Text.UTF8Encoding]::new($false))
                # Preserve exact streams for nested tools that mix their encodings with Sysmon's output.
                $stdoutPath = $script:NativeFailureLogPath + '.stdout.bin'
                $stderrPath = $script:NativeFailureLogPath + '.stderr.bin'
                Assert-NoReparsePoint $stdoutPath
                Assert-NoReparsePoint $stderrPath
                [IO.File]::WriteAllBytes($stdoutPath, $stdoutBytes.ToArray())
                [IO.File]::WriteAllBytes($stderrPath, $stderrBytes.ToArray())
            }
            $message = 'The recorder helper returned exit code {0}.' -f $process.ExitCode
            if ($detail) { $message += ' ' + $detail }
            if ($script:NativeFailureLogPath) { $message += ' Administrator diagnostics: ' + $script:NativeFailureLogPath }
            throw $message
        }
        return $output + $errorOutput
    } finally { $process.Dispose(); $stdoutBytes.Dispose(); $stderrBytes.Dispose() }
}

function New-ProcessRecorderConfiguration([string]$SchemaOutput) {
    $manifestStart = $SchemaOutput.IndexOf('<manifest ')
    $manifestEnd = $SchemaOutput.LastIndexOf('</manifest>')
    if ($manifestStart -lt 0 -or $manifestEnd -lt $manifestStart) { throw 'The signed Sysmon binary did not return its configuration schema.' }
    [xml]$schema = $SchemaOutput.Substring($manifestStart, $manifestEnd - $manifestStart + '</manifest>'.Length)
    $version = $schema.manifest.GetAttribute('schemaversion')
    if ($version -notmatch '^\d+\.\d+$') { throw 'Sysmon returned an invalid schema version.' }
    $document = [xml]('<Sysmon schemaversion="' + $version + '"><HashAlgorithms>SHA256</HashAlgorithms><EventFiltering /></Sysmon>')
    $filter = $document.SelectSingleNode('/Sysmon/EventFiltering')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($event in $schema.manifest.events.event) {
        $name = $event.GetAttribute('rulename')
        if ([string]::IsNullOrEmpty($name) -or -not $seen.Add($name)) { continue }
        $rule = $document.CreateElement($name)
        $mode = if ($name -in @('ProcessCreate', 'ProcessTerminate')) { 'exclude' } else { 'include' }
        $rule.SetAttribute('onmatch', $mode)
        $null = $filter.AppendChild($rule)
    }
    if (-not $seen.Contains('ProcessCreate') -or -not $seen.Contains('ProcessTerminate')) {
        throw 'This Sysmon schema does not support the required process events.'
    }
    return $document.OuterXml
}

function Get-RecorderChannel {
    try { return [Diagnostics.Eventing.Reader.EventLogConfiguration]::new($script:ChannelName) }
    catch [Diagnostics.Eventing.Reader.EventLogNotFoundException] { return $null }
}

function Get-RecorderFootprints {
    # Enumerations fail closed: errors are never treated as proof that an installation is absent.
    $footprints = [Collections.Generic.List[string]]::new()
    foreach ($key in Get-ChildItem -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction Stop) {
        if ($key.PSChildName -match '(?i)sysmon') { $footprints.Add('service-or-driver:' + $key.PSChildName) }
    }
    foreach ($directory in @($env:windir, (Join-Path $env:windir 'System32'), (Join-Path $env:windir 'System32\drivers'))) {
        foreach ($item in Get-ChildItem -LiteralPath $directory -Filter '*Sysmon*' -File -Force -ErrorAction Stop) {
            if ($item.Extension -in @('.exe', '.sys')) { $footprints.Add('binary:' + $item.FullName) }
        }
    }
    $channel = Get-RecorderChannel
    if ($channel) { $footprints.Add('event-channel:' + $script:ChannelName); $channel.Dispose() }
    $channelKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Channels\' + $script:ChannelName
    if (Test-Path -LiteralPath $channelKey -ErrorAction Stop) { $footprints.Add('event-channel-registration') }
    return $footprints.ToArray()
}

function Reset-CleanPendingInstallation($State, [string]$UserSid) {
    if (-not $State.installPending) { return $false }
    if ($State.owned -or $State.ownerSid -ne $UserSid) { return $false }
    $footprints = @(Get-RecorderFootprints)
    if ($footprints.Count -ne 0) { return $false }
    $State.installPending = $false
    Save-RecorderState $State
    return $true
}

function Repair-PendingInstallation($State, [string]$UserSid, [string]$VerifiedDownload) {
    if (-not $State.installPending) { return }
    if ($State.owned -or $State.ownerSid -ne $UserSid) {
        throw 'The interrupted recorder belongs to another owner or an established installation. Its machine state was preserved.'
    }
    if (Reset-CleanPendingInstallation $State $UserSid) { return }
    $source = [IO.Path]::GetFullPath((Join-Path $env:windir 'Sysmon64.exe'))
    $footprints = @(Get-RecorderFootprints)
    if ($footprints.Count -ne 1 -or $footprints[0] -ne ('binary:' + $source)) {
        throw ('The interrupted installation has service, driver, channel or binary footprints that cannot be recovered automatically: ' + ($footprints -join ', '))
    }
    Assert-NoReparsePoint $source
    Assert-MicrosoftSignature $source
    Assert-MicrosoftSignature $VerifiedDownload
    $expectedHash = (Get-FileHash -LiteralPath $VerifiedDownload -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'The leftover Sysmon binary differs from the verified Microsoft download. It was preserved.'
    }
    $installationStarted = [DateTime]::Parse($State.createdUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    if ((Get-Item -LiteralPath $source -Force).CreationTimeUtc -lt $installationStarted) {
        throw 'The leftover Sysmon binary predates this recorder setup. It was preserved.'
    }
    # Inspect every service/driver ImagePath, including renamed services, before moving an executable.
    foreach ($key in Get-ChildItem -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction Stop) {
        $imagePath = [string]$key.GetValue('ImagePath', [NullString]::Value, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($imagePath -match '(?i)sysmon64\.exe') {
            throw ('A service or driver still references the leftover Sysmon binary (' + $key.PSChildName + '). It was preserved.')
        }
    }
    $backupDirectory = [IO.Path]::GetFullPath((Join-Path $script:RecorderRoot 'recovery'))
    New-PrivateDirectory $backupDirectory
    $backup = [IO.Path]::GetFullPath((Join-Path $backupDirectory ('Sysmon64-' + [Guid]::NewGuid().ToString('N') + '.exe')))
    $allowedParent = [IO.Path]::GetFullPath($script:RecorderRoot).TrimEnd('\') + '\'
    if (-not $backup.StartsWith($allowedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($source, [IO.Path]::GetFullPath((Join-Path $env:windir 'Sysmon64.exe')), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The interrupted-install recovery paths are outside the expected directories.'
    }
    Assert-NoReparsePoint $backup
    if ($null -eq $State.PSObject.Properties['recoveryBackups']) {
        $State | Add-Member -MemberType NoteProperty -Name recoveryBackups -Value @()
    }
    $State.recoveryBackups = @($State.recoveryBackups) + [pscustomobject]@{
        source = $source; backup = $backup; sha256 = $expectedHash
        savedUtc = [DateTime]::UtcNow.ToString('o'); reason = 'Verified leftover from this owner''s interrupted installation'
    }
    Save-RecorderState $State
    # Both resolved absolute paths were checked above; the original is retained, not deleted.
    Move-Item -LiteralPath $source -Destination $backup -ErrorAction Stop
    $State.installPending = $false
    Save-RecorderState $State
}

function Add-ReaderAccess($State, [string]$UserSid) {
    $channel = Get-RecorderChannel
    if ($null -eq $channel) { throw 'The Sysmon event channel is unavailable.' }
    try {
        if (-not $channel.IsEnabled) { throw 'The existing Sysmon event channel is disabled. Its settings have been preserved.' }
        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($channel.SecurityDescriptor)
        if ($null -eq $descriptor.DiscretionaryAcl) { throw 'The event channel has no explicit access list; setup will not replace it.' }
        $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
        $hasDirectRead = $false
        foreach ($ace in $descriptor.DiscretionaryAcl) {
            if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $sid -and
                $ace.AceQualifier -eq 'AccessAllowed' -and (($ace.AccessMask -band 1) -eq 1)) { $hasDirectRead = $true }
        }
        if (-not $hasDirectRead) {
            # Persist intent first: a crash cannot leave an untracked access grant.
            if ($UserSid -notin @($State.addedReaderSids)) {
                $State.addedReaderSids = @($State.addedReaderSids) + $UserSid
                Save-RecorderState $State
            }
            $ace = [Security.AccessControl.CommonAce]::new('None', 'AccessAllowed', 1, $sid, $false, $null)
            $descriptor.DiscretionaryAcl.InsertAce($descriptor.DiscretionaryAcl.Count, $ace)
            $channel.SecurityDescriptor = $descriptor.GetSddlForm('All')
            $channel.SaveChanges()
        }
    } finally { $channel.Dispose() }
}

function Remove-ReaderAccess($State, [string]$UserSid) {
    if ($UserSid -notin @($State.addedReaderSids)) { return }
    $channel = Get-RecorderChannel
    if ($null -eq $channel) { return }
    try {
        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($channel.SecurityDescriptor)
        $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
        for ($i = $descriptor.DiscretionaryAcl.Count - 1; $i -ge 0; $i--) {
            $ace = $descriptor.DiscretionaryAcl[$i]
            if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $sid -and
                $ace.AceQualifier -eq 'AccessAllowed' -and $ace.AccessMask -eq 1 -and $ace.AceFlags -eq 'None') {
                $descriptor.DiscretionaryAcl.RemoveAce($i)
                break
            }
        }
        $channel.SecurityDescriptor = $descriptor.GetSddlForm('All')
        $channel.SaveChanges()
        $State.addedReaderSids = @($State.addedReaderSids | Where-Object { $_ -ne $UserSid })
        Save-RecorderState $State
    } finally { $channel.Dispose() }
}

function Get-TextHash([string]$Text) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}
