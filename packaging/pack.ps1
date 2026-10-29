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
# The package is stamped with the commit it was built from, so the tree must be exactly that commit.
if (git -C $root status --porcelain) { throw 'The working tree has changes: commit them first.' }
$feedPath = (Resolve-Path $Feed).Path
$common = @("-p:PhotinoNativeVersion=$NativeVersion", "-p:RestoreAdditionalProjectSources=$feedPath", '-c', 'Release', '-nologo')

dotnet test (Join-Path $root 'tests/Photino.NET.Tests/Photino.NET.Tests.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$commit = (git -C $root rev-parse HEAD).Trim()
# The repository URL in https form (an ssh remote becomes https://github.com/<owner>/<repo>).
$url = (git -C $root remote get-url origin).Trim() -replace '^git@([^:]+):', 'https://$1/' -replace '[.]git$', ''
dotnet pack (Join-Path $root 'Photino.NET/Photino.NET.csproj') @common -o $feedPath "-p:Version=$Version" `
    "-p:PackageVersion=$Version" "-p:RepositoryUrl=$url" "-p:RepositoryCommit=$commit" "-p:PackageProjectUrl=$url" `
    '-p:Description=A fork of Photino.NET with Windows hosting options (composition, host surfaces, hit regions, host input, popups); see FORK.md in the repository.' `
    '-p:PackageDescription=A fork of Photino.NET with Windows hosting options (composition, host surfaces, hit regions, host input, popups); see FORK.md in the repository.'
if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
