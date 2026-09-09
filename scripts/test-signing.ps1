#requires -Version 7.2
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\signing.ps1"

$testId = [Guid]::NewGuid().ToString('N')
$publisher = "CN=Remote Agent Link Signing Test $testId"
$root = Join-Path $RepoRoot "artifacts\signing-tests\$testId"
$output = Join-Path $root 'packages'
$state = Join-Path $root 'state\development.cer'
$certificates = [Collections.Generic.List[Security.Cryptography.X509Certificates.X509Certificate2]]::new()
$keyNames = [Collections.Generic.HashSet[string]]::new()
$passed = 0
$originalAppxFunction = Get-Item -LiteralPath Function:\Get-AppxPackage -ErrorAction SilentlyContinue
$originalPublicDiscovery = ${function:Get-PublicDevelopmentSigningHistory}
$originalCreation = ${function:New-PersistentDevelopmentCertificate}
$fakeInstalledPackages = @()
$fakeInstalledError = $null
$fakePublicHistory = @()
$fakePublicError = $null
$creationAttempts = 0

function Get-AppxPackage {
    [CmdletBinding()]
    param([string] $Name)
    if ($script:fakeInstalledError) { throw $script:fakeInstalledError }
    return $script:fakeInstalledPackages
}

function Assert-Test([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
    $script:passed++
}

function Assert-Failure([scriptblock] $Action, [string] $Pattern) {
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike $Pattern) { throw }
        $failed = $true
    }
    Assert-Test $failed "Expected failure: $Pattern"
}

function New-PublicFixture {
    param(
        [string] $Subject = $publisher,
        [DateTimeOffset] $NotBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset] $NotAfter = [DateTimeOffset]::UtcNow.AddDays(1),
        [string] $Eku = '1.3.6.1.5.5.7.3.3',
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags] $Usage = 'DigitalSignature',
        [int] $KeySize = 2048
    )
    $key = [Security.Cryptography.RSA]::Create($KeySize)
    $signed = $null
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject, $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($Usage, $true))
        if ($Eku) {
            $usages = [Security.Cryptography.OidCollection]::new()
            [void]$usages.Add([Security.Cryptography.Oid]::new($Eku))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
        }
        $signed = $request.CreateSelfSigned($NotBefore, $NotAfter)
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($signed.RawData)
        $certificates.Add($certificate)
        return $certificate
    }
    finally {
        if ($signed) { $signed.Dispose() }
        $key.Dispose()
    }
}

function Get-TestKeyName([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        Assert-Test ($rsa -is [Security.Cryptography.RSACng]) 'Expected a Windows CNG private key.'
        Assert-Test (!$rsa.Key.IsMachineKey) 'Test key must be current-user only.'
        Assert-Test ($rsa.Key.ExportPolicy -eq [Security.Cryptography.CngExportPolicies]::None) 'Development key must be non-exportable.'
        [void]$keyNames.Add($rsa.Key.KeyName)
        return $rsa.Key.KeyName
    }
    finally { if ($rsa) { $rsa.Dispose() } }
}

function Resolve-TestIdentity([string] $Packages = $output, [string] $Pin = $state) {
    $identity = Resolve-DevelopmentSigningIdentity -Publisher $publisher -OutputDirectory $Packages -StatePath $Pin
    $certificates.Add($identity.Certificate)
    return $identity
}

function New-UnsignedPackageFixture([string] $Path) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try { [void]$archive.CreateEntry('AppxManifest.xml') }
    finally { $archive.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    New-UnsignedPackageFixture (Join-Path $output 'RemoteAgentLink_0.0.0.1_x64.msix')
    $first = Resolve-TestIdentity
    $thumbprint = $first.Certificate.Thumbprint
    $keyName = Get-TestKeyName $first.Certificate
    Assert-Test ($first.StoreLocation -eq 'CurrentUser') 'First-time signer must be in CurrentUser\My.'
    Assert-Test (Test-Path -LiteralPath $state) 'First-time signing must save the public pin.'
    Assert-Test ([Security.Cryptography.CngKey]::Exists($keyName)) 'CNG signing key must persist.'
    $stateOnly = Resolve-TestIdentity (Join-Path $root 'no-packages') $state
    Assert-Test ($stateOnly.Certificate.Thumbprint -ceq $thumbprint) 'The public pin must work without any package outputs.'

    $childScript = @"
Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'
. '$($PSScriptRoot.Replace("'", "''"))\signing.ps1'
`$identity = Resolve-DevelopmentSigningIdentity -Publisher '$publisher' -OutputDirectory '$($output.Replace("'", "''"))' -StatePath '$($state.Replace("'", "''"))'
try { Write-Output `$identity.Certificate.Thumbprint } finally { `$identity.Certificate.Dispose() }
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childScript))
    $reused = & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -EncodedCommand $encoded
    Assert-Test ($LASTEXITCODE -eq 0 -and "$reused".Trim() -ceq $thumbprint) 'A new PowerShell process must reuse the persisted signing identity.'

    # Exercise the same signtool store selection without building or touching a real package.
    $payload = Join-Path $root 'signing-test.dll'
    Add-Type -TypeDefinition "public static class SigningFixture$testId { public static int Value => 1; }" -OutputAssembly $payload
    Invoke-Checked (Get-WindowsSdkTool 'signtool.exe') @('sign', '/fd', 'SHA256', '/sha1', $thumbprint, '/s', 'My', $payload)
    $signature = Get-AuthenticodeSignature -LiteralPath $payload
    Assert-Test ($signature.SignerCertificate.Thumbprint -ceq $thumbprint) 'Signtool must use the persisted test identity.'
    $signature.SignerCertificate.Dispose()

    Remove-Item -LiteralPath $state
    $recovered = Resolve-TestIdentity
    Assert-Test ($recovered.Certificate.Thumbprint -ceq $thumbprint) 'Cleaning public state must reuse the sole matching current-user identity.'
    $explicit = Get-PackageSigningIdentity -Thumbprint " $($thumbprint.ToLowerInvariant()) " -Publisher $publisher
    $certificates.Add($explicit.Certificate)
    Assert-Test ($explicit.Certificate.Thumbprint -ceq $thumbprint) 'Explicit current-user certificate selection must work.'
    Assert-Failure { Get-PackageSigningIdentity -Thumbprint $thumbprint -Publisher $publisher.ToLowerInvariant() } '*exactly match Publisher*'

    $publicOnly = New-PublicFixture
    $latest = Join-Path $root 'latest'
    New-Item -ItemType Directory -Path $latest | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $latest 'RemoteAgentLink_0.1.0.9_x64.cer'), $publicOnly.RawData)
    [IO.File]::WriteAllBytes((Join-Path $latest 'RemoteAgentLink_0.1.0.10_x64.cer'), $first.Certificate.RawData)
    New-UnsignedPackageFixture (Join-Path $latest 'RemoteAgentLink_0.1.0.12_x64.msix')
    $selected = Resolve-TestIdentity $latest (Join-Path $root 'latest-state\development.cer')
    Assert-Test ($selected.Certificate.Thumbprint -ceq $thumbprint) 'Highest numeric certificate version must win over older different signers; unsigned builds must not replace it.'
    [IO.File]::WriteAllBytes((Join-Path $latest 'RemoteAgentLink_0.1.0.11_x64.msix'), [byte[]]::new(0))
    Assert-Failure { Resolve-TestIdentity $latest (Join-Path $root 'missing-public.cer') } '*has no public .cer*'
    Remove-Item -LiteralPath (Join-Path $latest 'RemoteAgentLink_0.1.0.11_x64.msix')
    $signedWithoutPublic = Join-Path $latest 'RemoteAgentLink_0.1.0.11_x64.msix'
    New-UnsignedPackageFixture $signedWithoutPublic
    $archive = [IO.Compression.ZipFile]::Open($signedWithoutPublic, [IO.Compression.ZipArchiveMode]::Update)
    try { [void]$archive.CreateEntry('AppxSignature.p7x') } finally { $archive.Dispose() }
    Assert-Failure { Resolve-TestIdentity $latest (Join-Path $root 'missing-public.cer') } '*has no public .cer*'
    Remove-Item -LiteralPath $signedWithoutPublic

    $missing = Join-Path $root 'missing'
    New-Item -ItemType Directory -Path $missing | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $missing 'RemoteAgentLink_1.0.0.0_x64.cer'), $publicOnly.RawData)
    $missingPin = Join-Path $root 'missing-state\development.cer'
    Assert-Failure { Resolve-TestIdentity $missing $missingPin } "*$($publicOnly.Thumbprint) has no accessible matching private key*"
    Assert-Test (!(Test-Path -LiteralPath $missingPin)) 'Failure must not write signer state or choose another available signer.'
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($publicOnly)
        Assert-Failure { Resolve-TestIdentity $missing $missingPin } '*has no accessible matching private key*'
    }
    finally { $store.Dispose() }

    Assert-Failure { Resolve-TestIdentity $missing $state } '*Ambiguous development signing identity*'
    [IO.File]::WriteAllBytes((Join-Path $latest 'RemoteAgentLink_0.1.0.10_arm64.cer'), $publicOnly.RawData)
    Assert-Failure { Resolve-TestIdentity $latest (Join-Path $root 'ambiguous.cer') } '*Ambiguous development signing identity*'

    $expired = New-PublicFixture -NotBefore ([DateTimeOffset]::UtcNow.AddDays(-2)) -NotAfter ([DateTimeOffset]::UtcNow.AddDays(-1))
    [IO.File]::WriteAllBytes((Join-Path $missing 'RemoteAgentLink_1.0.0.0_x64.cer'), $expired.RawData)
    Assert-Failure { Resolve-TestIdentity $missing $missingPin } '*not currently valid*'
    $future = New-PublicFixture -NotBefore ([DateTimeOffset]::UtcNow.AddDays(1)) -NotAfter ([DateTimeOffset]::UtcNow.AddDays(2))
    Assert-Failure { Assert-SigningCertificate $future $publisher } '*not currently valid*'
    $noEku = New-PublicFixture -Eku ''
    Assert-Failure { Assert-SigningCertificate $noEku $publisher } '*Code Signing EKU*'
    $wrongEku = New-PublicFixture -Eku '1.3.6.1.5.5.7.3.1'
    Assert-Failure { Assert-SigningCertificate $wrongEku $publisher } '*Code Signing EKU*'
    $wrongUsage = New-PublicFixture -Usage KeyEncipherment
    Assert-Failure { Assert-SigningCertificate $wrongUsage $publisher } '*digital signatures*'
    $weakKey = New-PublicFixture -KeySize 1024
    Assert-Failure { Assert-SigningCertificate $weakKey $publisher } '*at least 2048 bits*'
    $corrupt = Join-Path $root 'corrupt.cer'
    [IO.File]::WriteAllText($corrupt, 'not a certificate')
    Assert-Failure { Resolve-TestIdentity $output $corrupt } '*Cannot read approved public signing certificate*'
    Assert-Failure { Get-PackageSigningIdentity -Thumbprint 'invalid' -Publisher $publisher } '*40 hexadecimal digits*'

    $historyPublisher = "CN=Remote Agent Link Signing History Test $testId"
    $historyState = Join-Path $root 'cleaned-history\development.cer'
    Set-Item -LiteralPath Function:\Get-PublicDevelopmentSigningHistory -Value {
        param([string] $Publisher)
        if ($script:fakePublicError) { throw $script:fakePublicError }
        return $script:fakePublicHistory
    }
    Set-Item -LiteralPath Function:\New-PersistentDevelopmentCertificate -Value {
        param([string] $Publisher)
        $script:creationAttempts++
        throw 'Unexpected certificate creation during history checks.'
    }
    try {
        foreach ($case in @(
            @{ Installed = @('installed package'); Public = @(); Pattern = '*existing installed RemoteAgentLink package*' },
            @{ Installed = @('installed package A', 'installed package B'); Public = @(); Pattern = '*existing installed RemoteAgentLink package*' },
            @{ Installed = @(); Public = @('public-only approved signer'); Pattern = '*Existing or ambiguous public signing identity*' },
            @{ Installed = @(); Public = @('expired signer A', 'approved signer B'); Pattern = '*Existing or ambiguous public signing identity*' }
        )) {
            $fakeInstalledPackages = $case.Installed
            $fakePublicHistory = $case.Public
            Assert-Failure {
                Resolve-DevelopmentSigningIdentity -Publisher $historyPublisher -OutputDirectory (Join-Path $root 'cleaned-packages') -StatePath $historyState
            } $case.Pattern
        }
        $fakeInstalledPackages = @()
        $fakePublicHistory = @()
        $fakeInstalledError = 'Simulated unreadable installed package inventory.'
        Assert-Failure {
            Resolve-DevelopmentSigningIdentity -Publisher $historyPublisher -OutputDirectory (Join-Path $root 'cleaned-packages') -StatePath $historyState
        } '*Cannot inspect existing installed RemoteAgentLink packages*'
        $fakeInstalledError = $null
        $fakePublicError = 'Simulated unreadable certificate store.'
        Assert-Failure {
            Resolve-DevelopmentSigningIdentity -Publisher $historyPublisher -OutputDirectory (Join-Path $root 'cleaned-packages') -StatePath $historyState
        } '*Cannot inspect prior public signing identities*'
        Assert-Test ($creationAttempts -eq 0) 'Prior or unreadable identity must stop before creating any certificate.'
        Assert-Test (!(Test-Path -LiteralPath $historyState)) 'History failures must not write a new public pin.'
    }
    finally {
        $fakeInstalledPackages = @()
        $fakePublicHistory = @()
        $fakeInstalledError = $null
        $fakePublicError = $null
        Set-Item -LiteralPath Function:\Get-PublicDevelopmentSigningHistory -Value $originalPublicDiscovery
        Set-Item -LiteralPath Function:\New-PersistentDevelopmentCertificate -Value $originalCreation
    }

    $second = New-PersistentDevelopmentCertificate $publisher
    $certificates.Add($second)
    $secondKeyName = Get-TestKeyName $second
    Assert-Failure { Resolve-TestIdentity $output (Join-Path $root 'ambiguous-store.cer') } '*Ambiguous development signing identity*'
    $deletedKey = [Security.Cryptography.CngKey]::Open($secondKeyName)
    try { $deletedKey.Delete() } finally { $deletedKey.Dispose() }
    Assert-Failure { Get-PackageSigningIdentity -Thumbprint $second.Thumbprint -Publisher $publisher } '*has no accessible matching private key*'

    Assert-Test (@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Extension -in @('.pfx', '.p12')).Count -eq 0) 'Tests must never write private-key exports.'
    Write-Host "Signing tests passed: $passed assertions; cross-process reuse and signtool signing verified without trust changes."
}
finally {
    if ($originalAppxFunction) { Set-Item -LiteralPath Function:\Get-AppxPackage -Value $originalAppxFunction.ScriptBlock }
    else { Remove-Item -LiteralPath Function:\Get-AppxPackage }
    Set-Item -LiteralPath Function:\Get-PublicDevelopmentSigningHistory -Value $originalPublicDiscovery
    Set-Item -LiteralPath Function:\New-PersistentDevelopmentCertificate -Value $originalCreation
    # The GUID subject is unique to this run; remove exact test certificates and their exact CNG keys only.
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($candidate in $store.Certificates) {
            try {
                if ($candidate.Subject -cne $publisher) { continue }
                if ($candidate.HasPrivateKey) {
                    $key = $null
                    try {
                        $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($candidate)
                        if ($key -is [Security.Cryptography.RSACng]) { [void]$keyNames.Add($key.Key.KeyName) }
                    }
                    catch {
                        if ($candidate.Thumbprint -notin @($certificates.Thumbprint)) { throw }
                    }
                    finally { if ($key) { $key.Dispose() } }
                }
                $store.Remove($candidate)
            }
            finally { $candidate.Dispose() }
        }
    }
    finally {
        $store.Dispose()
        foreach ($certificate in $certificates) { $certificate.Dispose() }
        foreach ($name in $keyNames) {
            if ([Security.Cryptography.CngKey]::Exists($name)) {
                $key = [Security.Cryptography.CngKey]::Open($name)
                try { $key.Delete() } finally { $key.Dispose() }
            }
        }
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
