using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace AgentLink.Broker;

internal static class PeerAdmission
{
    internal static void AdmitChannel(object trustGate, Func<IEnumerable<string>> currentPins,
        X509Certificate certificate, Action<bool> register)
    {
        lock (trustGate)
        {
            if (!EndpointIdentity.IsCertificateCurrent(certificate, DateTime.UtcNow))
                throw new AuthenticationException("The channel certificate has expired or is not valid yet.");
            // This entry point is only used inside same-logon DVC/WTS transport.
            // TLS verifies key possession; persisted fingerprint pins are not authorization.
            register(true);
        }
    }

    internal static void Admit(object trustGate, Func<IEnumerable<string>> currentPins,
        X509Certificate certificate, Action register)
    {
        lock (trustGate)
        {
            if (!EndpointIdentity.MatchesPinnedCertificate(certificate, currentPins(), DateTime.UtcNow))
                throw new AuthenticationException("Peer trust changed or expired before connection admission.");
            register();
        }
    }
}
