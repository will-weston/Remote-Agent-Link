#requires -Version 7.2
param(
    [Parameter(Mandatory)][string] $Package,
    [switch] $RequireSigned,
    [string] $Certificate
)
. "$PSScriptRoot\common.ps1"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('/', '\')
        if (!$entries.TryAdd($name, $entry)) { throw "Duplicate package entry: $name" }
        if ($name -match '\.(pfx|p12)$|(^|\\)(identity\.json|tasks\.db|copilot\.exe|runtime\.node)$') {
            throw "Unintended private state or CLI runtime in package: $name"
        }
    }
    function Read-PackageBytes([string] $Name) {
        if (!$entries.ContainsKey($Name)) { throw "Missing package entry: $Name" }
        $stream = $entries[$Name].Open()
        $memory = [IO.MemoryStream]::new()
        try { $stream.CopyTo($memory); return ,$memory.ToArray() }
        finally { $stream.Dispose(); $memory.Dispose() }
    }
    $mapBytes = Read-PackageBytes 'AppxBlockMap.xml'
    [xml]$map = [Text.Encoding]::UTF8.GetString($mapBytes)
    if ($map.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unsupported block-map hash algorithm.' }
    $mapped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $buffer = [byte[]]::new(65536)
    foreach ($file in $map.BlockMap.File) {
        $name = [string]$file.Name
        if (!$mapped.Add($name) -or !$entries.ContainsKey($name)) { throw "Invalid block-map entry: $name" }
        $stream = $entries[$name].Open()
        try {
            [long]$total = 0
            foreach ($block in $file.GetElementsByTagName('Block')) {
                $length = 0
                while ($length -lt $buffer.Length) {
                    $read = $stream.Read($buffer, $length, $buffer.Length - $length)
                    if ($read -eq 0) { break }
                    $length += $read
                }
                $actual = [Convert]::ToBase64String($sha.ComputeHash($buffer, 0, $length))
                if ($actual -ne $block.Hash) { throw "Block hash mismatch: $name" }
                $total += $length
            }
            if ($total -ne [long]$file.Size -or $stream.ReadByte() -ne -1) { throw "Block-map length mismatch: $name" }
        }
        finally { $stream.Dispose() }
    }
    foreach ($name in $entries.Keys) {
        if (!$mapped.Contains($name) -and $name -notin @('AppxBlockMap.xml', '[Content_Types].xml', 'AppxSignature.p7x', 'AppxMetadata\CodeIntegrity.cat')) {
            throw "Unmapped package payload: $name"
        }
    }
    foreach ($required in @(
        'AppxManifest.xml', 'Broker\AgentLink.Broker.exe', 'Broker\AgentLink.Transport.dll',
        'Broker\DesktopWorker\AgentLink.Desktop.Worker.exe', 'Broker\DesktopWorker\coreclr.dll',
        'Mcp\AgentLink.Mcp.exe', 'Native\AgentLink.RdpPlugin.exe', 'Native\AgentLink.ProxyStub.dll',
        'Public\channel.json'
    )) {
        if (!$mapped.Contains($required)) { throw "Missing covered payload: $required" }
    }

    [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-PackageBytes 'AppxManifest.xml')).TrimStart([char]0xFEFF)
    if ([version]$manifest.Package.Identity.Version -ge [version]'0.2.0.0') {
        foreach ($required in @(
            'Broker\Microsoft.UI.Xaml.dll', 'Broker\Microsoft.WindowsAppRuntime.dll', 'Broker\WinRT.Runtime.dll',
            'Broker\AgentLink.Broker.pri', 'Broker\App.xbf', 'Broker\MainWindow.xbf',
            'Broker\Views\ConnectionsView.xbf', 'Broker\Views\ActivityView.xbf', 'Broker\Views\SettingsView.xbf',
            'Broker\Assets\AgentLink.ico', 'Broker\Assets\Logo44.png',
            'licenses\WindowsAppSDK.txt', 'licenses\CsWinRT.txt', 'licenses\WebView2.txt', 'licenses\WebView2-NOTICE.txt'
        )) {
            if (!$mapped.Contains($required)) { throw "Missing covered WinUI payload or license: $required" }
        }
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $extensions = $manifest.SelectNodes(
        '//uap3:AppExtension[@Name="com.microsoft.rdp.plugin.wtsplugin"]', $namespaces)
    if ($extensions.Count -ne 1) { throw 'Expected one RDP app extension.' }
    $properties = $extensions[0].SelectSingleNode('uap3:Properties', $namespaces)
    if ($null -eq $properties) { throw 'RDP app extension properties are missing.' }
    $factory = $properties.SelectSingleNode('appx:ComServer', $namespaces)
    $channels = $properties.SelectNodes('appx:InitializationDvcName', $namespaces)
    if ($null -eq $factory -or $factory.InnerText -ne '583E23E9-3A9F-461C-A3CA-6F99F2625B78' -or
        $channels.Count -ne 1 -or $channels[0].InnerText -cne 'RALink.v1') {
        throw 'RDP app extension must declare the factory and RALink.v1 initialization channel.'
    }
    if ([version]$manifest.Package.Identity.Version -ge [version]'0.4.0.0') {
        $namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
        $namespaces.AddNamespace('virtualization', 'http://schemas.microsoft.com/appx/manifest/virtualization/windows10')
        $namespaces.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
        $classes = $manifest.SelectNodes('//com:ExeServer[@Executable="Native\AgentLink.RdpPlugin.exe"]/com:Class', $namespaces)
        $expectedClasses = @('583E23E9-3A9F-461C-A3CA-6F99F2625B78', '77A190C4-C7EB-4DB2-A317-9CFADA8F2A53')
        if ($classes.Count -ne 2 -or (Compare-Object $expectedClasses @($classes | ForEach-Object { $_.Id }))) {
            throw 'Modern and legacy activation must use distinct classes in the same packaged COM EXE.'
        }
        $excluded = $manifest.SelectNodes('/appx:Package/appx:Properties/virtualization:RegistryWriteVirtualization/virtualization:ExcludedKeys/virtualization:ExcludedKey', $namespaces)
        if ($excluded.Count -ne 1 -or $excluded[0].InnerText -cne 'HKEY_CURRENT_USER\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteAgentLink') {
            throw 'Legacy integration must exclude only its owned per-user AddIns leaf.'
        }
        $capabilities = $manifest.SelectNodes('/appx:Package/appx:Capabilities/rescap:Capability', $namespaces)
        if ($capabilities.Count -ne 2 -or (Compare-Object @('runFullTrust', 'unvirtualizedResources') @($capabilities | ForEach-Object { $_.Name }))) {
            throw 'Unexpected restricted capability set for dual COM loading.'
        }
        if ($manifest.SelectNodes('//*[local-name()="FileSystemWriteVirtualization" or local-name()="RegistryWriteVirtualization" and namespace-uri()!="http://schemas.microsoft.com/appx/manifest/virtualization/windows10"]').Count -ne 0) {
            throw 'Global registry or file-system virtualization changes are not allowed.'
        }
    }

    $signed = $entries.ContainsKey('AppxSignature.p7x')
    if ($RequireSigned -and !$signed) { throw 'A signed MSIX was required.' }
    if ($signed) {
        $signature = Read-PackageBytes 'AppxSignature.p7x'
        if ([Text.Encoding]::ASCII.GetString($signature, 0, 4) -ne 'PKCX') { throw 'Invalid MSIX signature header.' }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($signature[4..($signature.Length - 1)])
        $cms.CheckSignature($true)
        if ($cms.SignerInfos.Count -ne 1) { throw 'Expected one package signer.' }
        if ($Certificate) {
            $expected = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile((Resolve-Path -LiteralPath $Certificate))
            try {
                if ([Convert]::ToHexString($sha.ComputeHash($expected.RawData)) -ne
                    [Convert]::ToHexString($sha.ComputeHash($cms.SignerInfos[0].Certificate.RawData))) {
                    throw 'Package signer does not match the supplied public certificate.'
                }
            }
            finally { $expected.Dispose() }
        }
        $reader = [System.Formats.Asn1.AsnReader]::new([ReadOnlyMemory[byte]]::new($cms.ContentInfo.Content), [System.Formats.Asn1.AsnEncodingRules]::DER)
        $sequence = $reader.ReadSequence()
        [void]$sequence.ReadEncodedValue()
        $digestInfo = $sequence.ReadSequence()
        $algorithm = $digestInfo.ReadSequence()
        if ($algorithm.ReadObjectIdentifier() -ne '2.16.840.1.101.3.4.2.1') { throw 'Expected SHA-256 package signature digests.' }
        $digest = $digestInfo.ReadOctetString()
        if ([Text.Encoding]::ASCII.GetString($digest, 0, 4) -ne 'APPX' -or ($digest.Length - 4) % 36 -ne 0) {
            throw 'Invalid signed package digest structure.'
        }
        $digests = @{}
        for ($index = 4; $index -lt $digest.Length; $index += 36) {
            $tag = [Text.Encoding]::ASCII.GetString($digest, $index, 4)
            if ($digests.ContainsKey($tag)) { throw 'Duplicate signed package digest.' }
            $digests[$tag] = [Convert]::ToHexString($digest[($index + 4)..($index + 35)])
        }
        foreach ($binding in @(
            @{ Tag = 'AXBM'; Name = 'AppxBlockMap.xml' },
            @{ Tag = 'AXCT'; Name = '[Content_Types].xml' },
            @{ Tag = 'AXCI'; Name = 'AppxMetadata\CodeIntegrity.cat' }
        )) {
            if ($entries.ContainsKey($binding.Name)) {
                $bytes = Read-PackageBytes $binding.Name
                if ($digests[$binding.Tag] -ne [Convert]::ToHexString($sha.ComputeHash($bytes))) {
                    throw "Signed metadata digest mismatch: $($binding.Name)"
                }
            }
        }
    }
    Write-Host "Package payload: $($mapped.Count) mapped files; signed metadata verified: $signed."
    Write-Host 'These offline checks do not install trust or replace Windows installation/signature policy.'
}
finally { $sha.Dispose(); $archive.Dispose() }
