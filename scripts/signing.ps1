#requires -Version 7.2

function Read-PublicSigningCertificate {
    param([string] $Path)
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($bytes) -ne
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert) {
            throw 'Expected a public certificate, not private key material.'
        }
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)
    }
    catch {
        throw "Cannot read approved public signing certificate '$Path': $($_.Exception.Message) Restore the approved public certificate or explicitly select -SigningCertificateThumbprint; automatic rotation is disabled."
    }
}

function Assert-SigningCertificate {
    param(
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string] $Publisher
    )
    if ($Certificate.Subject -cne $Publisher) {
        throw "Signing certificate $($Certificate.Thumbprint) subject must exactly match Publisher '$Publisher'; found '$($Certificate.Subject)'."
    }
    $now = [DateTime]::UtcNow
    if ($now -lt $Certificate.NotBefore.ToUniversalTime() -or $now -ge $Certificate.NotAfter.ToUniversalTime()) {
        throw "Signing certificate $($Certificate.Thumbprint) is not currently valid (expired or not yet valid). Restore the approved identity or explicitly select a certificate with -SigningCertificateThumbprint; automatic rotation is disabled."
    }
    $usage = $Certificate.Extensions['2.5.29.15']
    $extendedUsage = $Certificate.Extensions['2.5.29.37']
    if (!$usage -or !($usage.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -or
        !$extendedUsage -or '1.3.6.1.5.5.7.3.3' -notin @($extendedUsage.EnhancedKeyUsages.Value)) {
        throw "Signing certificate $($Certificate.Thumbprint) must allow digital signatures and explicitly include the Code Signing EKU."
    }
    $constraints = $Certificate.Extensions['2.5.29.19']
    if ($constraints -and $constraints.CertificateAuthority) { throw 'A CA certificate cannot be used as the package signer.' }
    $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    try {
        if (!$key -or $key.KeySize -lt 2048) { throw 'Package signing requires an RSA certificate with a key of at least 2048 bits.' }
    }
    finally { if ($key) { $key.Dispose() } }
}

function Get-PackageSigningIdentity {
    param(
        [string] $Thumbprint,
        [string] $Publisher,
        [Security.Cryptography.X509Certificates.X509Certificate2] $ExpectedCertificate,
        [switch] $AllowMachineStore
    )
    $Thumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($Thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'SigningCertificateThumbprint must contain exactly 40 hexadecimal digits.' }
    if ($ExpectedCertificate) { Assert-SigningCertificate $ExpectedCertificate $Publisher }
    $locations = @('CurrentUser')
    if ($AllowMachineStore) { $locations += 'LocalMachine' }
    $failures = @()
    foreach ($location in $locations) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', $location)
        $matches = @()
        $selected = $null
        try {
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor
                    [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
            }
            catch {
                $failures += "${location}\My: $($_.Exception.Message)"
                continue
            }
            $matches = @($store.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Thumbprint, $false))
            foreach ($candidate in $matches) {
                Assert-SigningCertificate $candidate $Publisher
                if ($ExpectedCertificate -and
                    [Convert]::ToBase64String($candidate.RawData) -cne [Convert]::ToBase64String($ExpectedCertificate.RawData)) {
                    throw "Certificate $Thumbprint does not match the pinned public certificate."
                }
                $privateKey = $null
                $publicKey = $null
                try {
                    if (!$candidate.HasPrivateKey) { throw 'The certificate contains only its public key.' }
                    $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($candidate)
                    if (!$privateKey) { throw 'The RSA private key is unavailable.' }
                    $probe = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
                    $signature = $privateKey.SignData($probe, [Security.Cryptography.HashAlgorithmName]::SHA256,
                        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
                    $publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($candidate)
                    if (!$publicKey.VerifyData($probe, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                        [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Private key does not match the certificate.' }
                }
                catch {
                    $failures += "${location}\My: $($_.Exception.Message)"
                    continue
                }
                finally {
                    if ($privateKey) { $privateKey.Dispose() }
                    if ($publicKey) { $publicKey.Dispose() }
                }
                $selected = $candidate
                return [pscustomobject]@{ Certificate = $candidate; StoreLocation = $location }
            }
        }
        finally {
            foreach ($candidate in $matches) { if ($candidate -ne $selected) { $candidate.Dispose() } }
            $store.Dispose()
        }
    }
    $stores = ($locations | ForEach-Object { "$_\My" }) -join ' or '
    throw "Signing certificate $Thumbprint has no accessible matching private key in $stores. A .cer contains only the public certificate and cannot recover a discarded private key. Restore the matching certificate/private key from a secure backup, or explicitly choose an available signer with -SigningCertificateThumbprint (a different signer requires deliberate trust/update planning). No new signer was created. $($failures -join ' ')"
}

function New-PersistentDevelopmentCertificate {
    param([string] $Publisher)
    $parameters = [Security.Cryptography.CngKeyCreationParameters]::new()
    $parameters.Provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $parameters.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $parameters.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $parameters.Parameters.Add([Security.Cryptography.CngProperty]::new(
        'Length', [BitConverter]::GetBytes(3072), [Security.Cryptography.CngPropertyOptions]::None))
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,
        "RemoteAgentLink.DevelopmentSigning.$([Guid]::NewGuid().ToString('N'))", $parameters)
    $rsa = $null
    $certificate = $null
    $stored = $false
    try {
        $rsa = [Security.Cryptography.RSACng]::new($key)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Publisher, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $usages = [Security.Cryptography.OidCollection]::new()
        [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddMonths(6))
        $certificate.FriendlyName = 'Remote Agent Link development signing'
        Assert-SigningCertificate $certificate $Publisher
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($certificate)
            $stored = $true
        }
        finally { $store.Dispose() }
        return $certificate
    }
    finally {
        if (!$stored) {
            if ($certificate) { $certificate.Dispose() }
            $key.Delete()
        }
        if ($rsa) { $rsa.Dispose() }
        $key.Dispose()
    }
}

function Get-PublicDevelopmentSigningHistory {
    param([string] $Publisher)
    foreach ($location in @('CurrentUser', 'LocalMachine')) {
        foreach ($name in @('My', 'TrustedPeople', 'TrustedPublisher', 'Root', 'CA')) {
            $store = [Security.Cryptography.X509Certificates.X509Store]::new($name, $location)
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor
                    [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
                foreach ($certificate in $store.Certificates) {
                    try {
                        # Expired/public-only certificates still establish prior identity.
                        if ($certificate.Subject -eq $Publisher) {
                            "$($certificate.Thumbprint) (${location}\$name)"
                        }
                    }
                    finally { $certificate.Dispose() }
                }
            }
            catch { throw "Cannot read ${location}\${name}: $($_.Exception.Message)" }
            finally { $store.Dispose() }
        }
    }
}

function Assert-NoPriorDevelopmentSigningIdentity {
    param([string] $Publisher)
    try { $installed = @(Get-AppxPackage -Name 'RemoteAgentLink' -ErrorAction Stop) }
    catch {
        throw "Cannot inspect existing installed RemoteAgentLink packages: $($_.Exception.Message) First-time signing cannot be established; automatic signer creation is disabled. Restore the approved public pin or use -SigningCertificateThumbprint."
    }
    if ($installed.Count) {
        throw 'An existing installed RemoteAgentLink package prevents automatic first-time signer creation after artifact cleanup. Restore its approved public certificate/pin and matching private key, or explicitly use -SigningCertificateThumbprint; no new signer was created.'
    }
    try { $history = @(Get-PublicDevelopmentSigningHistory $Publisher) }
    catch {
        throw "Cannot inspect prior public signing identities: $($_.Exception.Message) First-time signing cannot be established; automatic signer creation is disabled. Restore the approved public pin or use -SigningCertificateThumbprint."
    }
    if ($history.Count) {
        throw "Existing or ambiguous public signing identity remains for Publisher '$Publisher': $($history -join ', '). Artifact cleanup does not authorize rotation. Restore the approved public certificate/pin and matching private key, or explicitly use -SigningCertificateThumbprint; no new signer was created."
    }
}

function Resolve-DevelopmentSigningIdentity {
    param(
        [string] $Publisher,
        [string] $OutputDirectory,
        [string] $StatePath
    )
    $certificates = [Collections.Generic.List[Security.Cryptography.X509Certificates.X509Certificate2]]::new()
    $identity = $null
    try {
        $paths = @()
        if (Test-Path -LiteralPath $StatePath) { $paths += $StatePath }
        # Compare numeric package versions, not modification times or lexicographic names.
        $outputs = @(if (Test-Path -LiteralPath $OutputDirectory) {
            Get-ChildItem -LiteralPath $OutputDirectory -File | ForEach-Object {
                if ($_.Name -match '^RemoteAgentLink_(\d+\.\d+\.\d+\.\d+)_[^.]+\.(cer|msix)$') {
                    [pscustomobject]@{ Version = [version]$Matches[1]; Path = $_.FullName }
                }
            }
        })
        foreach ($version in @($outputs | ForEach-Object Version | Sort-Object -Descending -Unique)) {
            $packagePaths = @()
            foreach ($output in @($outputs | Where-Object Version -EQ $version)) {
                $publicPath = [IO.Path]::ChangeExtension($output.Path, '.cer')
                if (!(Test-Path -LiteralPath $publicPath -PathType Leaf)) {
                    try {
                        $archive = [IO.Compression.ZipFile]::OpenRead($output.Path)
                        try {
                            if (!$archive.GetEntry('AppxManifest.xml') -or $archive.GetEntry('AppxSignature.p7x')) {
                                throw 'The package is signed or its manifest is missing.'
                            }
                        }
                        finally { $archive.Dispose() }
                    }
                    catch {
                        throw "Latest package output '$($output.Path)' has no public .cer and cannot be confirmed unsigned: $($_.Exception.Message) Restore its approved public certificate or explicitly use -SigningCertificateThumbprint; automatic signer selection cannot safely continue."
                    }
                    # An unsigned build does not establish or replace a signing identity.
                    continue
                }
                $packagePaths += $publicPath
            }
            if ($packagePaths.Count) { $paths += $packagePaths; break }
        }
        foreach ($path in @($paths | Sort-Object -Unique)) {
            $certificates.Add((Read-PublicSigningCertificate $path))
        }
        if (!$certificates.Count) {
            # Recover the pin from the user's store if public build artifacts were cleaned.
            $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                foreach ($candidate in $store.Certificates) {
                    if ($candidate.Subject -ceq $Publisher) { $certificates.Add($candidate) }
                    else { $candidate.Dispose() }
                }
            }
            finally { $store.Dispose() }
        }
        $distinct = @($certificates | ForEach-Object { [Convert]::ToBase64String($_.RawData) } | Sort-Object -CaseSensitive -Unique)
        if ($distinct.Count -gt 1) {
            throw "Ambiguous development signing identity: the latest package certificates, local signer state, or personal-store candidates disagree. Restore consistent approved public state or explicitly select -SigningCertificateThumbprint; no signer was created."
        }
        if (!$certificates.Count) {
            Assert-NoPriorDevelopmentSigningIdentity $Publisher
            $certificates.Add((New-PersistentDevelopmentCertificate $Publisher))
        }
        $expected = $certificates[0]
        $identity = Get-PackageSigningIdentity -Thumbprint $expected.Thumbprint -Publisher $Publisher `
            -ExpectedCertificate $expected -AllowMachineStore
        if (!(Test-Path -LiteralPath $StatePath)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $StatePath -Parent) | Out-Null
            # CreateNew never replaces an existing pin, including one written concurrently.
            $stream = [IO.File]::Open($StatePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($expected.RawData) }
            finally { $stream.Dispose() }
        }
        $pin = Read-PublicSigningCertificate $StatePath
        try {
            if ([Convert]::ToBase64String($pin.RawData) -cne [Convert]::ToBase64String($expected.RawData)) {
                throw 'Development signer state changed during selection. Refusing to sign with a different identity; retry after resolving the conflicting public pin.'
            }
        }
        finally { $pin.Dispose() }
        return $identity
    }
    catch {
        if ($identity) { $identity.Certificate.Dispose() }
        throw
    }
    finally { foreach ($certificate in $certificates) { $certificate.Dispose() } }
}
