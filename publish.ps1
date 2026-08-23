<#
.SYNOPSIS
    Builds Pulse Monitor for distribution.

.DESCRIPTION
    Runs the tests, then publishes the app. The self-contained profile produces a single
    .exe that needs no .NET runtime on the target machine; the framework-dependent
    profile produces a much smaller build for machines that already have it.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Profile FrameworkDependent
    .\publish.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('SelfContained', 'FrameworkDependent', 'Both')]
    [string]$Profile = 'SelfContained',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test Tool.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
}

$profiles = if ($Profile -eq 'Both') { @('SelfContained', 'FrameworkDependent') } else { @($Profile) }

foreach ($name in $profiles) {
    Write-Host "Publishing $name..." -ForegroundColor Cyan
    dotnet publish src/SysMon.App/SysMon.App.csproj -c Release "/p:PublishProfile=$name" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name." }
}

Write-Host 'Publishing the sensor probe...' -ForegroundColor Cyan
dotnet publish src/SysMon.Probe/SysMon.Probe.csproj -c Release -r win-x64 --self-contained false `
    -o publish/probe --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed for the probe.' }

# Referenced projects emit their symbols into the publish folder even when the profile sets
# DebugType=none, so strip them from what is meant to be a distributable folder.
Get-ChildItem -Path publish -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host ''
Write-Host 'Done. Output is under .\publish\' -ForegroundColor Green
Get-ChildItem -Path publish -Recurse -Filter *.exe | ForEach-Object {
    '{0,-52} {1,8:N1} MB' -f $_.FullName.Replace("$PSScriptRoot\", ''), ($_.Length / 1MB)
}
