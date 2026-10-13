<#
.SYNOPSIS
    Makes the Windows installer out of a published win-x64 folder.

.DESCRIPTION
        build\windows\installer.ps1 -Folder artifacts\<name>-<version>-win-x64

    The result, <name>-<version>-win-x64-setup.exe, lands beside the folder. The names come from
    Directory.Build.props; what the installer does is said in installer.iss. It needs Inno Setup
    (https://jrsoftware.org/isinfo.php, or: winget install JRSoftware.InnoSetup).

    -Set replaces names for one run, for a folder that was published with the same ones replaced
    (dotnet publish -p:FcProgramName=... -p:FcWindowsAppId=...): a trial copy that installs, updates
    and uninstalls beside the real app without touching it.

        build\windows\installer.ps1 -Folder <folder> -Set @{ ProgramName = 'Trial'; DisplayName = 'Trial'; AppId = '<a GUID of its own>'; Version = '0.9.0' }
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$OutputDir,
    [hashtable]$Set = @{}
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Folder = (Resolve-Path $Folder).Path
if (-not $OutputDir) { $OutputDir = Split-Path -Parent $Folder }

$compiler = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
    foreach ($base in ${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs") {
        foreach ($version in 7, 6) { "$base\Inno Setup $version\ISCC.exe" }
    }
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $compiler) { throw 'Inno Setup was not found. Install it (winget install JRSoftware.InnoSetup) and run this again.' }

# read as XML reads itself, so that the copyright sign arrives whole in every PowerShell
$props = New-Object xml
$props.Load((Join-Path $root 'Directory.Build.props'))
$app = $props.Project.PropertyGroup[0]
$defines = [ordered]@{
    Source      = $Folder
    Output      = $OutputDir
    Icon        = Join-Path $root 'FileConverter\Assets\app.ico'
    DisplayName = $app.FcDisplayName
    ProgramName = $app.FcProgramName
    Summary     = $app.FcSummary
    Website     = $app.FcWebsite
    Version     = $app.Version
    AppId       = $app.FcWindowsAppId
    Company     = $app.Company
    Copyright   = $app.Copyright.Replace('$(Company)', $app.Company)
}
foreach ($name in $Set.Keys) {
    if (-not $defines.Contains($name)) { throw "-Set: there is no '$name' (there are: $($defines.Keys -join ', '))" }
    $defines[$name] = $Set[$name]
}
$arguments = @($defines.GetEnumerator() | ForEach-Object { "/D$($_.Key)=$($_.Value)" }) + '/Qp' + (Join-Path $PSScriptRoot 'installer.iss')

& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$installer = Join-Path $OutputDir "$($defines.ProgramName)-$($defines.Version)-win-x64-setup.exe"
Write-Host ("{0}  {1:N1} MB" -f (Split-Path -Leaf $installer), ((Get-Item $installer).Length / 1MB))
