using System;
using Unison.UWPApp.Crypto;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Represents the authentication state for a WhatsApp session.
    /// Contains all cryptographic keys and session data.
    /// </summary>
    public class AuthState
    {
        /// <summary>
        /// Noise protocol static key pair
        /// </summary>
        public KeyPair NoiseKey { get; set; }

        /// <summary>
        /// Signal identity key pair (for signing)
        /// </summary>
        public KeyPair SignedIdentityKey { get; set; }

        /// <summary>
        /// Ephemeral key pair used during pairing
        /// </summary>
        public KeyPair PairingEphemeralKeyPair { get; set; }

        /// <summary>
        /// Signed pre-key for Signal protocol
        /// </summary>
        public SignedPreKeyData SignedPreKey { get; set; }

        /// <summary>
        /// Registration ID for Signal protocol
        /// </summary>
        public int RegistrationId { get; set; }

        /// <summary>
        /// ADV secret key for device registration (base64)
        /// </summary>
        public string AdvSecretKey { get; set; }

        /// <summary>
        /// Current pairing code (8 chars)
        /// </summary>
        public string PairingCode { get; set; }

        /// <summary>
        /// Routing info received from server
        /// </summary>
        public byte[] RoutingInfo { get; set; }

        /// <summary>
        /// Next pre-key ID to generate
        /// </summary>
        public int NextPreKeyId;

        /// <summary>
        /// User info (JID, name, phone) after successful login
        /// </summary>
        public UserInfo Me { get; set; }

        /// <summary>
        /// Store for one-time pre-keys (KeyId -> PreKeyData)
        /// </summary>
        public System.Collections.Generic.Dictionary<int, PreKeyData> PreKeys { get; set; } = new System.Collections.Generic.Dictionary<int, PreKeyData>();

        /// <summary>
        /// Store for Signal sessions (JID -> SessionData)
        /// TODO: Implement SessionData structure
        /// </summary>
        public System.Collections.Generic.Dictionary<string, byte[]> Sessions { get; set; } = new System.Collections.Generic.Dictionary<string, byte[]>();

        /// <summary>
        /// Account info from server
        /// </summary>
        public AccountInfo Account { get; set; }

        /// <summary>
        /// Whether the device is fully registered
        /// </summary>
        public bool Registered { get; set; }

        /// <summary>
        /// Last received property hash
        /// </summary>
        public string LastPropHash { get; set; }

        /// <summary>
        /// Creates a new auth state with fresh keys
        /// </summary>
        public static AuthState Create()
        {
            // IMPORTANT: signedIdentityKey must be X25519 (for DH/key exchange), NOT Ed25519 (for signing)
            // Baileys uses Curve.generateKeyPair() which generates X25519 keys
            var identityKey = CryptoUtils.GenerateKeyPair();
            var signedPreKey = CreateSignedPreKey(identityKey, 1);

            return new AuthState
            {
                NoiseKey = CryptoUtils.GenerateKeyPair(),
                SignedIdentityKey = identityKey,
                PairingEphemeralKeyPair = CryptoUtils.GenerateKeyPair(),
                SignedPreKey = signedPreKey,
                RegistrationId = CryptoUtils.GenerateRegistrationId(),
                AdvSecretKey = Convert.ToBase64String(CryptoUtils.RandomBytes(32)),
                NextPreKeyId = 1,
                Registered = false
            };
        }

        /// <summary>
        /// Creates a signed pre-key
        /// </summary>
        private static SignedPreKeyData CreateSignedPreKey(KeyPair identityKey, int keyId)
        {
            var preKey = CryptoUtils.GenerateKeyPair();
            var pubKeyWithPrefix = CryptoUtils.GenerateSignalPubKey(preKey.Public);
            var signature = CryptoUtils.Sign(identityKey.Private, pubKeyWithPrefix);

            return new SignedPreKeyData
            {
                KeyId = keyId,
                KeyPair = preKey,
                Signature = signature
            };
        }

        /// <summary>
        /// Rotates the signed pre-key
        /// </summary>
        public void RotateSignedPreKey()
        {
            var newKeyId = (SignedPreKey?.KeyId ?? 0) + 1;
            SignedPreKey = CreateSignedPreKey(SignedIdentityKey, newKeyId);
        }
    }

    /// <summary>
    /// Signed pre-key data
    /// </summary>
    public class SignedPreKeyData
    {
        public int KeyId { get; set; }
        public KeyPair KeyPair { get; set; }
        public byte[] Signature { get; set; }
    }

    /// <summary>
    /// User info (logged in user)
    /// </summary>
    public class UserInfo
    {
        public string Id { get; set; }  // JID
        public string Name { get; set; }
        public string Lid { get; set; } // Linked ID
    }

    /// <summary>
    /// One-time pre-key data
    /// </summary>
    public class PreKeyData
    {
        public int Id { get; set; }
        public KeyPair KeyPair { get; set; }

        public static PreKeyData Generate(int id)
        {
            return new PreKeyData
            {
                Id = id,
                KeyPair = CryptoUtils.GenerateKeyPair()
            };
        }
    }

    /// <summary>
    /// Account info from server
    /// </summary>
    public class AccountInfo
    {
        public byte[] Details { get; set; }
        public byte[] AccountSignatureKey { get; set; }
        public byte[] AccountSignature { get; set; }
        public byte[] DeviceSignature { get; set; }
    }
}
