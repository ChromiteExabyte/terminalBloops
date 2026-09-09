#Requires -Version 5.1
# Event-channel tests use memory doubles; atomic metadata writes use an isolated temporary fixture.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Recorder.Common.ps1')
$actualSaveState = ${function:Save-RecorderState}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$script:saveCount = 0
function Save-RecorderState($State) { $script:saveCount++ }
function Get-RecorderChannel { return $script:testChannel }

$testSid = 'S-1-5-21-100-200-300-1001'
$originalSddl = 'O:BAG:SYD:(A;;0x7;;;SY)(A;;0x7;;;BA)(A;;0x1;;;S-1-5-21-100-200-300-1002)'
$originalSddl = ([Security.AccessControl.RawSecurityDescriptor]::new($originalSddl)).GetSddlForm('All')
$script:testChannel = [pscustomobject]@{ IsEnabled = $true; SecurityDescriptor = $originalSddl; Changes = 0 }
$script:testChannel | Add-Member -MemberType ScriptMethod -Name SaveChanges -Value { $this.Changes++ }
$script:testChannel | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
$state = [pscustomobject]@{ addedReaderSids = @() }
Add-ReaderAccess $state $testSid
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($script:testChannel.SecurityDescriptor)
Assert ($descriptor.DiscretionaryAcl.Count -eq 4) 'Reader grant did not preserve the existing ACEs.'
Assert ($descriptor.DiscretionaryAcl[3].AccessMask -eq 1) 'Reader grant includes rights beyond read.'
Assert ($state.addedReaderSids.Count -eq 1) 'Reader grant was not tracked.'
Assert ($script:saveCount -eq 1) 'Ownership must be saved before applying the access grant.'

Add-ReaderAccess $state $testSid
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($script:testChannel.SecurityDescriptor)
Assert ($descriptor.DiscretionaryAcl.Count -eq 4) 'Repeated setup duplicated a reader ACE.'
Remove-ReaderAccess $state $testSid
Assert ($script:testChannel.SecurityDescriptor -eq $originalSddl) 'Removal did not preserve the unrelated ACL exactly.'
Assert ($state.addedReaderSids.Count -eq 0) 'Removed reader grant remained tracked.'
Remove-ReaderAccess $state $testSid
Assert ($script:testChannel.SecurityDescriptor -eq $originalSddl) 'Repeated removal changed an unrelated ACE.'

# Do not claim or remove an access grant already provided by the administrator.
$existingSid = 'S-1-5-21-100-200-300-1002'
Add-ReaderAccess $state $existingSid
Assert ($state.addedReaderSids.Count -eq 0) 'A pre-existing reader ACE was incorrectly claimed.'
Remove-ReaderAccess $state $existingSid
Assert ($script:testChannel.SecurityDescriptor -eq $originalSddl) 'A pre-existing reader ACE was removed.'

# If another administrator broadens our ACE, removal must leave their changed ACE alone.
Add-ReaderAccess $state $testSid
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($script:testChannel.SecurityDescriptor)
$descriptor.DiscretionaryAcl[3].AccessMask = 3
$script:testChannel.SecurityDescriptor = $descriptor.GetSddlForm('All')
Remove-ReaderAccess $state $testSid
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($script:testChannel.SecurityDescriptor)
Assert ($descriptor.DiscretionaryAcl.Count -eq 4 -and $descriptor.DiscretionaryAcl[3].AccessMask -eq 3) 'A subsequently modified ACE was removed.'

Assert ((Get-TextHash 'abc') -eq 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD') 'Configuration fingerprint failed its known-vector test.'
$rejected = $false
try { Assert-UserDataDirectory ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) (Join-Path $env:windir 'TerminalBloops') | Out-Null }
catch { $rejected = $true }
Assert $rejected 'An elevated output directory outside the requested user profile was accepted.'
$sampleSchema = '<manifest schemaversion="4.91"><events><event value="1" rulename="ProcessCreate"/><event value="5" rulename="ProcessTerminate"/><event value="12" rulename="RegistryEvent"/><event value="13" rulename="RegistryEvent"/><event value="99" rulename="FutureEvent"/><event value="255"/></events></manifest>'
[xml]$configuration = New-ProcessRecorderConfiguration $sampleSchema
$rules = $configuration.SelectNodes('/Sysmon/EventFiltering/*')
Assert ($rules.Count -eq 4) 'Schema generation did not deduplicate rule names or skip unfilterable diagnostics.'
Assert ($configuration.SelectNodes('/Sysmon/EventFiltering/*[@onmatch="exclude"]').Count -eq 2) 'Configuration enabled event classes beyond process starts/exits.'
Assert ($configuration.SelectSingleNode('/Sysmon/EventFiltering/FutureEvent').GetAttribute('onmatch') -eq 'include') 'A future event class was not disabled.'

# Decode redirected output before parsing: Sysmon can emit UTF-16 while ordinary tools emit ASCII/UTF-8.
foreach ($encoding in @([Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode, [Text.Encoding]::UTF8)) {
    [byte[]]$encoded = $encoding.GetBytes($sampleSchema)
    [byte[]]$withBom = $encoding.GetPreamble() + $encoded
    foreach ($bytes in @($encoded, $withBom)) {
        $decoded = Convert-NativeBytesToText $bytes
        Assert ($decoded -eq $sampleSchema) ('Native output failed round-trip decoding: ' + $encoding.WebName)
        [xml]$decodedConfig = New-ProcessRecorderConfiguration $decoded
        Assert ($decodedConfig.Sysmon.schemaversion -eq '4.91') 'UTF-16/NUL schema fixture failed end-to-end parsing.'
    }
}
Assert ((Convert-NativeBytesToText ([Text.Encoding]::ASCII.GetBytes('Native ASCII output'))) -eq 'Native ASCII output') 'ASCII native output changed.'
$nativeOutput = Invoke-QuietProcess (Join-Path $env:windir 'System32\cmd.exe') @('/d', '/c', 'ver')
Assert ($nativeOutput -match '\d+\.\d+') 'Generic ASCII cmd output failed byte-stream capture.'

function Get-RecorderFootprints { return $script:testFootprints }
$script:testFootprints = @()
$pending = [pscustomobject]@{ installPending = $true; owned = $false; ownerSid = $testSid }
Assert (Reset-CleanPendingInstallation $pending $testSid) 'Same-owner, footprint-free setup did not become retryable.'
Assert (-not $pending.installPending) 'Retryable pending flag did not clear.'
$pending.installPending = $true
Assert (-not (Reset-CleanPendingInstallation $pending $existingSid)) 'Another owner could clear pending ownership.'
$script:testFootprints = @('binary:C:\Windows\Sysmon64.exe')
Assert (-not (Reset-CleanPendingInstallation $pending $testSid)) 'A remaining installation footprint was ignored.'
Assert $pending.installPending 'A remaining binary lost its pending ownership marker.'

# Windows PowerShell5.1 converts $null to an empty backup path for File.Replace;
# exercise two real writes so that this recovery-breaking binding regression stays covered.
$savedRoot = $script:RecorderRoot
$savedStatePath = $script:StatePath
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('TerminalBloops.ScriptTests-' + [Guid]::NewGuid().ToString('N'))))
try {
    $null = [IO.Directory]::CreateDirectory($fixture)
    $script:RecorderRoot = $fixture
    $script:StatePath = Join-Path $fixture 'installation.json'
    & $actualSaveState ([pscustomobject]@{ installPending = $true })
    & $actualSaveState ([pscustomobject]@{ installPending = $false })
    $reloaded = Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
    Assert (-not $reloaded.installPending) 'The second atomic metadata write did not persist.'
} finally {
    $script:RecorderRoot = $savedRoot
    $script:StatePath = $savedStatePath
    $temporaryFile = Join-Path $fixture 'installation.json'
    if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    if (Test-Path -LiteralPath $fixture) { [IO.Directory]::Delete($fixture, $false) }
}
Write-Output 'Recorder helper tests passed: ACL preservation, grant ownership, fingerprint, path rejection, event filtering, recovery guards and atomic metadata updates. No machine settings changed.'
