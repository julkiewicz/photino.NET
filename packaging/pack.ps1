# Tests Photino.NET against a Photino.Native package from a feed folder, then packs Photino.NET into that folder.
#   pwsh packaging/pack.ps1 -Version 4.0.17-fork.1 -NativeVersion 4.0.23-fork.1 -Feed <feed folder>
# The feed must already hold Photino.Native <NativeVersion> (photino.Native's packaging/windows/pack-windows.ps1).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$NativeVersion,
    [Parameter(Mandatory = $true)][string]$Feed
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$feedPath = (Resolve-Path $Feed).Path
$common = @("-p:PhotinoNativeVersion=$NativeVersion", "-p:RestoreAdditionalProjectSources=$feedPath", '-c', 'Release', '-nologo')

dotnet test (Join-Path $root 'tests/Photino.NET.Tests/Photino.NET.Tests.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$commit = (git -C $root rev-parse HEAD).Trim()
$url = (git -C $root remote get-url origin).Trim()
dotnet pack (Join-Path $root 'Photino.NET/Photino.NET.csproj') @common -o $feedPath "-p:Version=$Version" `
    "-p:PackageVersion=$Version" "-p:RepositoryUrl=$url" "-p:RepositoryCommit=$commit"
if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
