<#
.SYNOPSIS
    Builds the downloads: one self-contained folder and archive per system.

.DESCRIPTION
    Each build carries only its own system's parts (windowing backend, platform code, native
    libraries) and its own copy of .NET, so nothing has to be installed first.

        build\publish.ps1                    # both systems
        build\publish.ps1 -Runtime win-x64   # just Windows
        build\publish.ps1 -Runtime linux-x64

    Results land in artifacts\ : <name>-<version>-<runtime>\ and the matching .zip / .tar.gz, the
    name and version being the ones in Directory.Build.props. A Linux build made on Windows is a
    real Linux build (.NET cross-publishes), packed with the right file permissions by build\Pack.cs.
    The Windows build is also made into an installer, <name>-<version>-win-x64-setup.exe, when
    Inno Setup is installed (see build\windows\installer.ps1).
#>
param(
    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string]$Runtime = 'all',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$app = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup[0]

$runtimes = if ($Runtime -eq 'all') { @('win-x64', 'linux-x64') } else { @($Runtime) }
foreach ($rid in $runtimes) {
    $name = "$($app.FcProgramName)-$($app.Version)-$rid"
    $folder = Join-Path $artifacts $name
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }

    Write-Host "Publishing $name ..."
    & $Dotnet publish (Join-Path $root 'FileConverter\FileConverter.csproj') -c Release -r $rid -o $folder --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $archive = Join-Path $artifacts ($name + $(if ($rid -like 'win-*') { '.zip' } else { '.tar.gz' }))
    & $Dotnet run (Join-Path $root 'build\Pack.cs') -- $folder $archive
    if ($LASTEXITCODE -ne 0) { throw "packing failed for $rid" }

    $size = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("  {0}: {1:N1} MB unpacked" -f $name, $size)

    # Windows also gets an installer, where this machine can make one
    if ($rid -like 'win-*' -and $IsLinux -ne $true) {
        try { & (Join-Path $PSScriptRoot 'windows\installer.ps1') -Folder $folder }
        catch { Write-Warning "No installer was made: $($_.Exception.Message)" }
    }
}
