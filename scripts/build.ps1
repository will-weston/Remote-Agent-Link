#requires -Version 7.2
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [switch] $ManagedOnly,
    [switch] $NativeOnly
)
. "$PSScriptRoot\common.ps1"
if ($ManagedOnly -and $NativeOnly) { throw 'Choose either ManagedOnly or NativeOnly, not both.' }
Push-Location $RepoRoot
try {
    if (!$ManagedOnly) {
        $vs = Get-VisualStudio
        $command = 'set "PATH={0};%PATH%" && call "{1}" >nul && "{2}" "{3}" /m /nologo /v:minimal /p:Configuration={4} /p:Platform=x64 /p:PlatformToolset={5} /p:WindowsTargetPlatformVersion=10.0' -f
            $vs.VsWhereDirectory, $vs.VcVars, $vs.MSBuild, "$RepoRoot\tests\Native.Tests\Native.Tests.vcxproj", $Configuration, $vs.Toolset
        Invoke-Checked $env:ComSpec @('/d', '/c', $command)
    }
    if (!$NativeOnly) {
        & "$PSScriptRoot\generate-assets.ps1"
        foreach ($project in @('AgentLink.Broker', 'AgentLink.Mcp')) {
            Invoke-Checked 'dotnet' @('build', ".\src\$project\$project.csproj", '-c', $Configuration, '--nologo', '--verbosity', 'quiet')
        }
    }
}
finally { Pop-Location }
