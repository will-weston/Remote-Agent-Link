#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Get-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path $vswhere)) { throw 'Install Visual Studio with Desktop development with C++ and a Windows SDK.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$installation) { throw 'No Visual Studio installation with x64 C++ tools was found.' }
    $msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
    $toolsets = Get-ChildItem (Join-Path $installation 'MSBuild\Microsoft\VC') -Directory -Recurse |
        Where-Object { $_.Parent.Name -eq 'PlatformToolsets' -and $_.Name -match '^v\d+$' -and $_.FullName -match '\\x64\\' } |
        Sort-Object { [int]$_.Name.Substring(1) } -Descending
    if (!$toolsets) { throw 'No MSVC x64 platform toolset was found.' }
    return @{
        MSBuild = $msbuild
        Toolset = $toolsets[0].Name
        VcVars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
        VsWhereDirectory = Split-Path $vswhere -Parent
    }
}

function Get-WindowsSdkTool {
    param([string] $Name)
    $kits = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
    $versions = Get-ChildItem (Join-Path $kits 'bin') -Directory |
        Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending
    foreach ($version in $versions) {
        $tool = Join-Path $version.FullName "x64\$Name"
        if (Test-Path $tool) { return $tool }
    }
    throw "$Name was not found in an installed Windows SDK."
}
