#requires -Version 7.2
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string] $Version = '0.4.0.1',
    [string] $Publisher = 'CN=Remote Agent Link Development',
    [switch] $DevSign,
    [string] $SigningCertificateThumbprint,
    [switch] $SkipBuild
)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\signing.ps1"
if ($DevSign -and $SigningCertificateThumbprint) { throw 'Choose development signing or an existing signing certificate.' }
Push-Location $RepoRoot
$certificate = $null
try {
    $output = Join-Path $RepoRoot 'artifacts\packages'
    if ($DevSign -or $SigningCertificateThumbprint) {
        if ($DevSign) {
            $identity = Resolve-DevelopmentSigningIdentity -Publisher $Publisher -OutputDirectory $output `
                -StatePath (Join-Path $RepoRoot 'artifacts\signing\development.cer')
        }
        else {
            $identity = Get-PackageSigningIdentity -Thumbprint $SigningCertificateThumbprint -Publisher $Publisher
        }
        $certificate = $identity.Certificate
        $signtool = Get-WindowsSdkTool 'signtool.exe'
        Write-Host "Signing certificate: $($certificate.Thumbprint) ($($identity.StoreLocation)\My)"
    }
    if (!$SkipBuild) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }
    Invoke-Checked ".\artifacts\native\$Configuration\Native.Tests.exe" @('--manifest-only')
    $makeappx = Get-WindowsSdkTool 'makeappx.exe'
    $stage = Join-Path $RepoRoot "artifacts\package\$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $stage, $output | Out-Null
    foreach ($project in @('AgentLink.Broker', 'AgentLink.Mcp')) {
        $folder = $project.Replace('AgentLink.', '')
        Invoke-Checked 'dotnet' @(
            'publish', ".\src\$project\$project.csproj", '-c', $Configuration,
            '-r', 'win-x64', '--self-contained', 'true', '-o', "$stage\$folder",
            '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '--nologo', '--verbosity', 'quiet'
        )
    }
    # A separate self-contained worker must not depend on a framework installed
    # on the packaging machine or on the broker's runtimeconfig.
    Invoke-Checked 'dotnet' @(
        'publish', '.\src\AgentLink.Desktop\Worker\AgentLink.Desktop.Worker.csproj',
        '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-o', "$stage\Broker\DesktopWorker", '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false', '--nologo', '--verbosity', 'quiet'
    )
    New-Item -ItemType Directory -Force -Path "$stage\Native", "$stage\Assets", "$stage\Public" | Out-Null
    foreach ($binary in @('AgentLink.RdpPlugin.exe', 'AgentLink.ProxyStub.dll')) {
        Copy-Item -LiteralPath "$RepoRoot\artifacts\native\$Configuration\$binary" -Destination "$stage\Native\$binary"
    }
    Copy-Item -LiteralPath "$RepoRoot\src\Native\AgentLink.ProxyStub\THIRD-PARTY-NOTICES.txt" -Destination "$stage\Native\THIRD-PARTY-NOTICES.txt"
    Copy-Item -LiteralPath "$RepoRoot\artifacts\native\$Configuration\AgentLink.Transport.dll" -Destination "$stage\Broker\AgentLink.Transport.dll"
    Copy-Item -LiteralPath "$RepoRoot\LICENSE", "$RepoRoot\THIRD-PARTY-NOTICES" -Destination $stage
    Copy-Item -LiteralPath "$RepoRoot\licenses" -Destination "$stage\licenses" -Recurse
    Copy-Item -LiteralPath "$RepoRoot\packaging\AgentLink.Msix\Public\channel.json" -Destination "$stage\Public\channel.json"
    Invoke-Checked "$stage\Native\AgentLink.RdpPlugin.exe" @('--check-startup')
    [xml]$manifest = Get-Content -LiteralPath "$RepoRoot\packaging\AgentLink.Msix\Package.appxmanifest" -Raw
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Save("$stage\AppxManifest.xml")

    & "$PSScriptRoot\generate-assets.ps1"
    foreach ($size in @(44, 50, 150)) {
        Copy-Item -LiteralPath "$RepoRoot\artifacts\branding\Logo$size.png" -Destination "$stage\Assets\Logo$size.png"
    }
    $diagnostic = [System.Diagnostics.ProcessStartInfo]::new("$stage\Broker\AgentLink.Broker.exe")
    $diagnostic.ArgumentList.Add('--check-installation')
    $diagnostic.UseShellExecute = $false
    $diagnostic.RedirectStandardOutput = $true
    $diagnostic.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($diagnostic)
    try {
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Packaged payload diagnostics timed out.'
        }
        if ($process.ExitCode -ne 0) { throw $standardError.GetAwaiter().GetResult() }
        Write-Host $standardOutput.GetAwaiter().GetResult().Trim()
    }
    finally { $process.Dispose() }
    Invoke-Checked 'dotnet' @(
        'run', '--project', '.\tests\Mcp.Tests\Mcp.Tests.csproj', '-c', $Configuration,
        '--verbosity', 'quiet', '--', "$stage\Mcp\AgentLink.Mcp.exe"
    )
    $package = Join-Path $output "RemoteAgentLink_${Version}_x64.msix"
    $packLog = & $makeappx pack /d $stage /p $package /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($packLog -join "`n") }

    if ($DevSign -or $SigningCertificateThumbprint) {
        $signArguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My')
        if ($identity.StoreLocation -eq 'LocalMachine') { $signArguments += '/sm' }
        Invoke-Checked $signtool ($signArguments + $package)
        [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($package, '.cer'),
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    $verification = @{ Package = $package; RequireSigned = [bool]($DevSign -or $SigningCertificateThumbprint) }
    if ($certificate) { $verification.Certificate = [IO.Path]::ChangeExtension($package, '.cer') }
    & "$PSScriptRoot\test-package.ps1" @verification
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$package.sha256", "$hash  $([IO.Path]::GetFileName($package))`n")
    Write-Host "Package: $package"
    Write-Host "SHA-256: $hash"
    Write-Host "Layout: $stage"
    Write-Host 'No certificate trust or package installation was performed.'
}
finally {
    if ($certificate) { $certificate.Dispose() }
    Pop-Location
}
