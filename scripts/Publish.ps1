#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$outputDirectory = Join-Path $root 'artifacts\publish'
Push-Location $root
try {
    & $dotnet publish (Join-Path $root 'src\TerminalBloops.App\TerminalBloops.App.csproj') --configuration $Configuration --runtime win-x64 --self-contained true --output $outputDirectory -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed with exit code {0}.' -f $LASTEXITCODE) }
    $scriptDirectory = Join-Path $outputDirectory 'scripts'
    $null = New-Item -ItemType Directory -Path $scriptDirectory -Force
    foreach ($name in @('Setup-Recorder.ps1', 'Remove-Recorder.ps1', 'Diagnose-Recorder.ps1', 'Recorder.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scriptDirectory $name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $outputDirectory 'README.md') -Force
    Write-Output ('Published self-contained Windows x64 app: ' + (Join-Path $outputDirectory 'TerminalBloops.exe'))
} finally { Pop-Location }
