// =============================================================================
// WaVoipDtlsClient
//
// DTLS 1.2 client for the WA relay: self-signed cert, server cert not verified
// (media auth is HBH SRTP). Ports meowcaller relay.ConnectRelayMedia DTLS half.
// =============================================================================
using System;
using System.Collections;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace Unison.Voip.Engine.Relay
{
    public sealed class WaVoipDtlsClient : DefaultTlsClient
    {
        private readonly WaVoipIdentity _identity;

        public WaVoipDtlsClient(WaVoipIdentity identity)
            : base(identity.Crypto)
        {
            _identity = identity;
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new Auth(this);
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

        public override void NotifyAlertRaised(short alertLevel, short alertDescription, string message, Exception cause)
        {
            LastAlert = alertDescription + (message != null ? ": " + message : string.Empty) +
                        (cause != null ? " (" + cause.Message + ")" : string.Empty);
        }

        public string LastAlert { get; private set; }

        private sealed class Auth : TlsAuthentication
        {
            private readonly WaVoipDtlsClient _client;

            public Auth(WaVoipDtlsClient client)
            {
                _client = client;
            }

            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                // InsecureSkipVerify — media is authenticated by HBH SRTP, not DTLS.
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                SignatureAndHashAlgorithm algorithm = PreferEcdsaSha256(
                    certificateRequest.SupportedSignatureAlgorithms);
                var cryptoParams = new TlsCryptoParameters(_client.m_context);
                return _client._identity.CreateSigner(cryptoParams, algorithm);
            }

            private static SignatureAndHashAlgorithm PreferEcdsaSha256(IList supported)
            {
                var ecdsaSha256 = SignatureAndHashAlgorithm.GetInstance(
                    HashAlgorithm.sha256,
                    SignatureAlgorithm.ecdsa);
                if (supported == null || supported.Count == 0)
                {
                    return ecdsaSha256;
                }

                foreach (object item in supported)
                {
                    var alg = item as SignatureAndHashAlgorithm;
                    if (alg != null &&
                        alg.Signature == SignatureAlgorithm.ecdsa &&
                        alg.Hash == HashAlgorithm.sha256)
                    {
                        return alg;
                    }
                }

                foreach (object item in supported)
                {
                    var alg = item as SignatureAndHashAlgorithm;
                    if (alg != null && alg.Signature == SignatureAlgorithm.ecdsa)
                    {
                        return alg;
                    }
                }

                return ecdsaSha256;
            }
        }
    }
}
