#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $root
try {
    & $dotnet build (Join-Path $root 'src\TerminalBloops.App\TerminalBloops.App.csproj') --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw ('Build failed with exit code {0}.' -f $LASTEXITCODE) }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-RecorderHelpers.ps1')
    if ($LASTEXITCODE -ne 0) { throw ('Recorder helper tests failed with exit code {0}.' -f $LASTEXITCODE) }
    $testProject = Join-Path $root 'tests\TerminalBloops.Tests\TerminalBloops.Tests.csproj'
    if (Test-Path -LiteralPath $testProject) {
        & $dotnet test $testProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw ('Tests failed with exit code {0}.' -f $LASTEXITCODE) }
    }
} finally { Pop-Location }
