// =============================================================================
// KeyBundleNodes
//
// Builds the key material nodes that go inside a <keys> block.
//
// These appear whenever we have to hand someone the means to open a session with
// us: the prekey upload, and the retry receipt that says "you could not read my
// message, here is a fresh key, try again". The ids are three-byte big-endian
// because that is what the server expects, not because it is a natural size.
//
// Ports: rc14 xmppPreKey / xmppSignedPreKey in src/Utils/signal.ts
// =============================================================================
using System;
using System.Collections.Generic;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;

namespace Unison.Socket.Signal
{
    public static class KeyBundleNodes
    {
        /// <summary>Signal's Curve25519 key bundle marker, sent as the type child.</summary>
        public static readonly byte[] KeyBundleType = { 5 };

        public static BinaryNode PreKey(int keyId, byte[] publicKey)
        {
            return new BinaryNode("key", null, new List<BinaryNode>
            {
                new BinaryNode("id", null, EncodeBigEndian(keyId, 3)),
                new BinaryNode("value", null, publicKey)
            });
        }

        public static BinaryNode SignedPreKey(SignedPreKeyData signedPreKey)
        {
            if (signedPreKey == null || signedPreKey.KeyPair == null)
            {
                return null;
            }

            return new BinaryNode("skey", null, new List<BinaryNode>
            {
                new BinaryNode("id", null, EncodeBigEndian(signedPreKey.KeyId, 3)),
                new BinaryNode("value", null, signedPreKey.KeyPair.Public),
                new BinaryNode("signature", null, signedPreKey.Signature)
            });
        }

        public static byte[] EncodeBigEndian(long value, int width = 4)
        {
            var bytes = new byte[width];
            for (var i = width - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }

            return bytes;
        }

        /// <summary>
        /// Serialises the signed device identity that proves this companion belongs to the
        /// account. Dropping the account signature key produces the shorter form the server
        /// wants in most stanzas.
        /// </summary>
        public static byte[] EncodeSignedDeviceIdentity(AccountInfo account, bool includeSignatureKey)
        {
            if (account == null)
            {
                return null;
            }

            var identity = new global::Proto.ADVSignedDeviceIdentity
            {
                Details = Google.Protobuf.ByteString.CopyFrom(account.Details ?? Array.Empty<byte>()),
                AccountSignature = Google.Protobuf.ByteString.CopyFrom(account.AccountSignature ?? Array.Empty<byte>()),
                DeviceSignature = Google.Protobuf.ByteString.CopyFrom(account.DeviceSignature ?? Array.Empty<byte>())
            };

            if (includeSignatureKey && account.AccountSignatureKey != null && account.AccountSignatureKey.Length > 0)
            {
                identity.AccountSignatureKey = Google.Protobuf.ByteString.CopyFrom(account.AccountSignatureKey);
            }

            return identity.ToByteArray();
        }
    }
}
