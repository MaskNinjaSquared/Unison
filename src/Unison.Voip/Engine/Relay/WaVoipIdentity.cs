// =============================================================================
// WaVoipIdentity
//
// Self-signed ECDSA P-256 cert + key for the WA VoIP DTLS client (and the
// local test server). meowcaller: selfsign.GenerateSelfSignedWithDNS("wa-voip").
// =============================================================================
using System;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace Unison.Voip.Engine.Relay
{
    public sealed class WaVoipIdentity
    {
        private WaVoipIdentity(
            BcTlsCrypto crypto,
            AsymmetricCipherKeyPair keyPair,
            Certificate certificate)
        {
            Crypto = crypto;
            KeyPair = keyPair;
            Certificate = certificate;
        }

        public BcTlsCrypto Crypto { get; }

        public AsymmetricCipherKeyPair KeyPair { get; }

        public Certificate Certificate { get; }

        public static WaVoipIdentity Create(string commonName = "wa-voip")
        {
            var random = new SecureRandom();
            var crypto = new BcTlsCrypto(random);

            var gen = new ECKeyPairGenerator();
            gen.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, random));
            AsymmetricCipherKeyPair keyPair = gen.GenerateKeyPair();

            var name = new X509Name("CN=" + (commonName ?? "wa-voip"));
            var certGen = new X509V3CertificateGenerator();
            BigInteger serial = BigInteger.ProbablePrime(120, random);
            certGen.SetSerialNumber(serial);
            certGen.SetIssuerDN(name);
            certGen.SetSubjectDN(name);
            DateTime now = DateTime.UtcNow;
            certGen.SetNotBefore(now.AddDays(-1));
            certGen.SetNotAfter(now.AddYears(2));
            certGen.SetPublicKey(keyPair.Public);

            var signer = new Asn1SignatureFactory("SHA256withECDSA", keyPair.Private, random);
            X509Certificate bcCert = certGen.Generate(signer);

            var tlsCert = new BcTlsCertificate(crypto, bcCert.CertificateStructure);
            var certificate = new Certificate(new TlsCertificate[] { tlsCert });
            return new WaVoipIdentity(crypto, keyPair, certificate);
        }

        public TlsCredentialedSigner CreateSigner(TlsCryptoParameters cryptoParams, SignatureAndHashAlgorithm algorithm)
        {
            return new BcDefaultTlsCredentialedSigner(
                cryptoParams,
                Crypto,
                KeyPair.Private,
                Certificate,
                algorithm);
        }
    }
}
