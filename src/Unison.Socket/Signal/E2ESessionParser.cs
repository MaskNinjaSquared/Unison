// =============================================================================
// E2ESessionParser
//
// Reads the prekey bundles the server returns and opens sessions from them.
//
// The reply to an "encrypt" query is a list of users, each carrying an identity
// key, a signed prekey and usually a one-time prekey, with the ids encoded as
// big-endian byte strings rather than numbers.
//
// Deviation from rc14: Baileys prefixes each public key with Signal's 0x05 type
// byte because its libsignal expects that form. Unison's SignalHandler works
// with the raw 32 bytes as they come off the wire, so the keys are passed
// through untouched - prefixing them here would break every session it opens.
//
// Ports: rc14 parseAndInjectE2ESessions in src/Utils/signal.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;

namespace Unison.Socket.Signal
{
    public static class E2ESessionParser
    {
        /// <summary>
        /// Opens a session for every user in the reply. Users the server refused - an error
        /// child instead of keys - are skipped rather than failing the whole batch.
        /// </summary>
        public static async Task<int> ParseAndInjectAsync(BinaryNode response, ISignalRepository repository)
        {
            if (response == null || repository == null)
            {
                return 0;
            }

            var list = response.GetChild("list");
            if (list == null)
            {
                return 0;
            }

            var injected = 0;
            foreach (var user in list.GetChildren("user"))
            {
                var bundle = ReadBundle(user);
                if (bundle == null)
                {
                    continue;
                }

                await repository.InjectE2ESessionAsync(bundle.Jid, bundle).ConfigureAwait(false);
                injected++;
            }

            return injected;
        }

        /// <summary>
        /// Reads the prekey bundle a peer attaches to a retry receipt, or null when it attached
        /// none. A peer that cannot read us often sends its current keys along with the
        /// complaint, and opening the session from those is what makes the resend readable
        /// without asking the server for a bundle that may be the same broken one.
        /// The registration id sits on the receipt, not inside the keys node.
        /// </summary>
        public static PreKeyBundle ReadRetryReceiptBundle(BinaryNode receipt, string jid)
        {
            if (receipt == null)
            {
                return null;
            }

            var keys = receipt.GetChild("keys");
            if (keys == null)
            {
                return null;
            }

            var identity = ReadBytes(keys, "identity");
            var signedKey = keys.GetChild("skey");
            if (identity == null || identity.Length != 32 || signedKey == null)
            {
                return null;
            }

            var signedPublic = ReadBytes(signedKey, "value");
            var signature = ReadBytes(signedKey, "signature");
            if (signedPublic == null || signedPublic.Length != 32 || signature == null)
            {
                return null;
            }

            var bundle = new PreKeyBundle
            {
                Jid = jid,
                RegistrationId = (uint)ReadBigEndian(receipt, "registration", 4),
                IdentityKey = identity,
                SignedPreKey = signedPublic,
                SignedPreKeyId = (uint)ReadBigEndian(signedKey, "id", 3),
                SignedPreKeySignature = signature
            };

            var oneTime = keys.GetChild("key");
            if (oneTime != null)
            {
                var oneTimePublic = ReadBytes(oneTime, "value");
                if (oneTimePublic == null || oneTimePublic.Length != 32)
                {
                    return null;
                }

                bundle.OneTimePreKey = oneTimePublic;
                bundle.OneTimePreKeyId = (uint)ReadBigEndian(oneTime, "id", 3);
            }

            return bundle;
        }

        /// <summary>Reads one user node, or null when it carries an error or is incomplete.</summary>
        public static PreKeyBundle ReadBundle(BinaryNode user)
        {
            if (user == null || user.GetChild("error") != null)
            {
                return null;
            }

            var jid = user.GetAttribute("jid");
            var identity = ReadBytes(user, "identity");
            var signedKey = user.GetChild("skey");
            if (string.IsNullOrEmpty(jid) || identity == null || signedKey == null)
            {
                return null;
            }

            var signedPublic = ReadBytes(signedKey, "value");
            var signature = ReadBytes(signedKey, "signature");
            if (signedPublic == null || signature == null)
            {
                return null;
            }

            var bundle = new PreKeyBundle
            {
                Jid = jid,
                RegistrationId = (uint)ReadBigEndian(user, "registration", 4),
                IdentityKey = identity,
                SignedPreKey = signedPublic,
                SignedPreKeyId = (uint)ReadBigEndian(signedKey, "id", 3),
                SignedPreKeySignature = signature
            };

            // The one-time prekey is optional: the server runs out of them, and a session can
            // still be opened from the signed prekey alone.
            var oneTime = user.GetChild("key");
            if (oneTime != null)
            {
                var oneTimePublic = ReadBytes(oneTime, "value");
                if (oneTimePublic != null)
                {
                    bundle.OneTimePreKey = oneTimePublic;
                    bundle.OneTimePreKeyId = (uint)ReadBigEndian(oneTime, "id", 3);
                }
            }

            return bundle;
        }

        private static byte[] ReadBytes(BinaryNode parent, string tag)
        {
            var child = parent.GetChild(tag);
            return child != null ? child.GetContentBytes() : null;
        }

        /// <summary>Reads a fixed-width big-endian integer stored as node content.</summary>
        private static long ReadBigEndian(BinaryNode parent, string tag, int width)
        {
            var bytes = ReadBytes(parent, tag);
            if (bytes == null || bytes.Length == 0)
            {
                return 0;
            }

            long value = 0;
            var start = Math.Max(0, bytes.Length - width);
            for (var i = start; i < bytes.Length; i++)
            {
                value = (value << 8) | bytes[i];
            }

            return value;
        }
    }
}
