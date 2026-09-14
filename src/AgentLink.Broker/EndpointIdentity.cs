using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using AgentLink.Core;

namespace AgentLink.Broker;

internal sealed record IdentitySettings(string Thumbprint, string[] TrustedFingerprints);

internal sealed class EndpointIdentity : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _lockPath;
    private readonly object _gate = new();
    private IdentitySettings _settings;

    public EndpointIdentity(string directory)
    {
        EnsurePrivateDirectory(directory);
        _settingsPath = Path.Combine(directory, "identity.json");
        _lockPath = Path.Combine(directory, "identity.lock");
        using var creationLock = AcquireFileLock();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (File.Exists(_settingsPath))
        {
            _settings = JsonSerializer.Deserialize<IdentitySettings>(File.ReadAllText(_settingsPath), Json.Options)
                ?? throw new InvalidDataException("Invalid endpoint identity configuration.");
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, _settings.Thumbprint, false);
            Certificate = matches.Count == 1 && matches[0].HasPrivateKey
                ? matches[0] : throw new InvalidOperationException("The endpoint certificate or private key is missing. Restore it before reconnecting.");
        }
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN=Remote Agent Link {Guid.NewGuid():N}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            var pfx = generated.Export(X509ContentType.Pfx);
            try
            {
                Certificate = X509CertificateLoader.LoadPkcs12(pfx, null,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
            Certificate.FriendlyName = "Remote Agent Link endpoint";
            store.Add(Certificate);
            _settings = new(Certificate.Thumbprint, []);
            Save();
        }
        if (DateTime.UtcNow < Certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= Certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("The endpoint certificate has expired or is not valid yet. Check the device clock or replace the expired endpoint identity.");
    }

    public X509Certificate2 Certificate { get; }
    internal string StorageDirectory => Path.GetDirectoryName(_settingsPath)!;
    public string Fingerprint => FingerprintOf(Certificate);
    public event Action<string>? UntrustedPeer;
    public event Action? TrustChanged;
    public IReadOnlyList<string> TrustedFingerprints { get { lock (_gate) return _settings.TrustedFingerprints.ToArray(); } }

    internal static string FingerprintOf(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    internal EndpointIdentity(string directory, X509Certificate2 certificate)
    {
        EnsurePrivateDirectory(directory);
        _settingsPath = Path.Combine(directory, "identity.json");
        _lockPath = Path.Combine(directory, "identity.lock");
        Certificate = new X509Certificate2(certificate);
        using var creationLock = AcquireFileLock();
        _settings = File.Exists(_settingsPath) ? ReadSettings() : new(Certificate.Thumbprint, []);
        if (!File.Exists(_settingsPath))
            Save();
    }

    // Channel authentication verifies certificate validity; SslStream still performs
    // the TLS signatures/proof of possession. Admission additionally requires the
    // existing authenticated same-logon DVC/WTS transport and endpoint hello.
    public bool ValidateChannelPeer(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && IsCertificateCurrent(certificate, DateTime.UtcNow);

    public bool ValidatePeer(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        RefreshTrust();
        if (certificate is null)
            return false;
        var fingerprint = FingerprintOf(certificate);
        lock (_gate)
        {
            if (MatchesPinnedCertificate(certificate, _settings.TrustedFingerprints, DateTime.UtcNow))
                return true;
        }
        UntrustedPeer?.Invoke(fingerprint);
        return false;
    }

    internal static bool MatchesPinnedCertificate(X509Certificate certificate, IEnumerable<string> pins, DateTime utcNow)
    {
        return pins.Contains(FingerprintOf(certificate), StringComparer.Ordinal) && IsCertificateCurrent(certificate, utcNow);
    }

    internal static bool IsCertificateCurrent(X509Certificate certificate, DateTime utcNow)
    {
        try
        {
            using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return utcNow >= peer.NotBefore.ToUniversalTime() && utcNow < peer.NotAfter.ToUniversalTime();
        }
        catch (CryptographicException) { return false; }
    }

    internal void AdmitChannel(X509Certificate certificate, Action<bool> register)
    {
        PeerAdmission.AdmitChannel(_gate, () => _settings.TrustedFingerprints, certificate, register);
    }

    public void AdmitPeer(X509Certificate certificate, Action register)
    {
        RefreshTrust();
        PeerAdmission.Admit(_gate, () => _settings.TrustedFingerprints, certificate, register);
    }

    public void RequireTrustedFingerprint(string fingerprint)
        => WithTrustedFingerprint(fingerprint, () => { });

    internal void WithTrustedFingerprint(string fingerprint, Action action)
    {
        RefreshTrust();
        lock (_gate)
        {
            if (!_settings.TrustedFingerprints.Contains(fingerprint, StringComparer.Ordinal))
                throw new AgentLinkException("untrusted_peer", "The selected endpoint is no longer paired.");
            action();
        }
    }

    public void Trust(string fingerprint)
    {
        fingerprint = NormalizeFingerprint(fingerprint);
        if (fingerprint == Fingerprint)
            throw new ArgumentException("Trust the other machine's fingerprint, not this endpoint's own fingerprint.");
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            var latest = ReadSettings();
            Save(latest with { TrustedFingerprints = latest.TrustedFingerprints.Append(fingerprint).Distinct().ToArray() });
        }
        NotifyTrustChanged();
    }

    public void Revoke(string fingerprint)
    {
        fingerprint = NormalizeFingerprint(fingerprint);
        var changed = false;
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            var latest = ReadSettings();
            var remaining = latest.TrustedFingerprints.Where(pin => pin != fingerprint).ToArray();
            changed = remaining.Length != latest.TrustedFingerprints.Length ||
                !_settings.TrustedFingerprints.SequenceEqual(latest.TrustedFingerprints, StringComparer.Ordinal);
            Save(latest with { TrustedFingerprints = remaining });
        }
        if (changed)
            NotifyTrustChanged();
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        fingerprint = fingerprint.Trim().Replace(":", "").ToUpperInvariant();
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Enter the peer's complete 64-character SHA-256 fingerprint.");
        return fingerprint;
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            Save(ReadSettings() with { TrustedFingerprints = [] });
        }
        NotifyTrustChanged();
    }

    public void RefreshTrust()
    {
        var changed = false;
        lock (_gate)
        {
            var latest = ReadSettings();
            if (!_settings.TrustedFingerprints.SequenceEqual(latest.TrustedFingerprints, StringComparer.Ordinal))
            {
                _settings = latest;
                changed = true;
            }
        }
        if (changed)
            NotifyTrustChanged();
    }

    private void NotifyTrustChanged()
    {
        if (TrustChanged is not { } handlers)
            return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception) { BrokerDiagnostics.ObserverFailed(BrokerObserver.TrustChanged, exception); }
        }
    }

    private IdentitySettings ReadSettings()
    {
        var settings = JsonSerializer.Deserialize<IdentitySettings>(File.ReadAllText(_settingsPath), Json.Options)
            ?? throw new InvalidDataException("Invalid endpoint identity configuration.");
        if (settings.Thumbprint != Certificate.Thumbprint || settings.TrustedFingerprints is null)
            throw new InvalidDataException("Endpoint identity changed. Restore the expected identity and restart the broker.");
        return settings;
    }

    private FileStream AcquireFileLock()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32 && started.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
    }

    private void Save(IdentitySettings? next = null)
    {
        next ??= _settings;
        var temporary = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json.Options));
            File.Move(temporary, _settingsPath, true);
            _settings = next;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void EnsurePrivateDirectory(string directory) => PrivateStorage.EnsureDirectory(directory);

    public void Dispose() => Certificate.Dispose();
}
