// =============================================================================
// WaVoipDtlsServer
//
// Minimal DTLS 1.2 server for local handshake tests (not used against a live
// WhatsApp relay — that side is the remote DTLS server).
// =============================================================================
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace Unison.Voip.Engine.Relay
{
    public sealed class WaVoipDtlsServer : DefaultTlsServer
    {
        private readonly WaVoipIdentity _identity;

        public WaVoipDtlsServer(WaVoipIdentity identity)
            : base(identity.Crypto)
        {
            _identity = identity;
        }

        protected override ProtocolVersion[] GetSupportedVersions()
        {
            return new ProtocolVersion[] { ProtocolVersion.DTLSv12 };
        }

        protected override int[] GetSupportedCipherSuites()
        {
            return new int[]
            {
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
                CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256
            };
        }

        protected override TlsCredentialedSigner GetECDsaSignerCredentials()
        {
            var cryptoParams = new TlsCryptoParameters(m_context);
            return _identity.CreateSigner(
                cryptoParams,
                SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
        }

        // No CertificateRequest — WA relay path does not need mutual auth for the test server.
        // The live client still presents a cert when the relay asks.
    }
}
