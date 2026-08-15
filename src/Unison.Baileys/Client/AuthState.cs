using System;
using Unison.Baileys.Crypto;

namespace Unison.Baileys.Client
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
        /// Tracks which device JIDs have already received our sender key for a group.
        /// Key: group JID, Value: participant device JIDs.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> SenderKeyMemory { get; set; } =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();

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
        /// Current app-state sync key id (base64 keyId bytes)
        /// </summary>
        public string MyAppStateKeyId { get; set; }

        /// <summary>
        /// Last account-sync timestamp acknowledged from dirty notifications.
        /// </summary>
        public long LastAccountSyncTimestamp { get; set; }

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
    /// User info (logged in user). AvatarUrl is a local/remote URI; null = no photo.
    /// Domain UI mirror: <c>Unison.Core.Models.Profile</c>.
    /// </summary>
    public class UserInfo
    {
        public string Id { get; set; }  // JID
        public string Name { get; set; }
        /// <summary>PN digits from the account JID — not a display name.</summary>
        public string Phone { get; set; }
        public string Lid { get; set; } // Linked ID
        public string AvatarUrl { get; set; }
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

    /// <summary>
    /// Signal pre-key bundle returned by the WhatsApp encrypt query. It lives in
    /// the shared protocol layer so SignalHandler has no dependency on SocketClient.
    /// </summary>
    public class PreKeyBundle
    {
        public string Jid { get; set; }
        public uint RegistrationId { get; set; }
        public byte[] IdentityKey { get; set; }
        public byte[] SignedPreKey { get; set; }
        public uint SignedPreKeyId { get; set; }
        public byte[] SignedPreKeySignature { get; set; }
        public byte[] OneTimePreKey { get; set; }
        public uint? OneTimePreKeyId { get; set; }
    }
}
