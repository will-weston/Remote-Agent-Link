#requires -Version 7.2
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [ValidateSet('All', 'Core', 'Desktop', 'Copilot', 'Mcp', 'Ui', 'Native', 'Signing')][string[]] $Suite = @('All'),
    [switch] $SkipBuild
)
. "$PSScriptRoot\common.ps1"
Push-Location $RepoRoot
try {
    if (!$SkipBuild) {
        if ('All' -in $Suite) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }
        else {
            if ('Native' -in $Suite) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration -NativeOnly }
            if ('Mcp' -in $Suite) {
                Invoke-Checked 'dotnet' @('build', '.\src\AgentLink.Mcp\AgentLink.Mcp.csproj', '-c', $Configuration, '--nologo', '--verbosity', 'quiet')
            }
        }
    }
    $projects = @{
        Core = 'AgentLink.Tests'
        Desktop = 'Desktop.Tests'
        Copilot = 'Copilot.Tests'
        Mcp = 'Mcp.Tests'
        Ui = 'Broker.Ui.Tests'
    }
    foreach ($name in @('Core', 'Desktop', 'Copilot', 'Mcp', 'Ui')) {
        if ('All' -notin $Suite -and $name -notin $Suite) { continue }
        $project = $projects[$name]
        $arguments = @('run', '--project', ".\tests\$project\$project.csproj", '-c', $Configuration, '--verbosity', 'quiet')
        if ($name -eq 'Mcp') {
            $arguments += @('--', ".\src\AgentLink.Mcp\bin\$Configuration\net10.0-windows\AgentLink.Mcp.dll")
        }
        Invoke-Checked 'dotnet' $arguments
    }
    if ('All' -in $Suite -or 'Native' -in $Suite) {
        Invoke-Checked ".\artifacts\native\$Configuration\Native.Tests.exe" @()
    }
    if ('All' -in $Suite -or 'Signing' -in $Suite) {
        & "$PSScriptRoot\test-signing.ps1"
    }
}
finally { Pop-Location }
