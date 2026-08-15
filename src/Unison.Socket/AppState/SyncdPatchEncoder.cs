// =============================================================================
// SyncdPatchEncoder
//
// Builds the patch that tells the phone the user changed something.
//
// It is the decoder run backwards, and every step has to match it exactly: the
// same key expansion, the same MACs over the same bytes, the same contribution
// to the running hash. A patch the server accepts but that leaves our hash
// disagreeing with the phone's is the worst outcome, because the next sync fails
// its MAC check and the collection has to be rebuilt from a snapshot.
//
// The version is bumped before the snapshot MAC is computed, since the MAC
// covers the state the patch produces rather than the one it started from.
//
// Ports: rc14 encodeSyncdPatch in src/Utils/chat-utils.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Crypto;
using Unison.Socket.Abstractions;

namespace Unison.Socket.AppState
{
    public sealed class EncodedAppPatch
    {
        public global::Proto.SyncdPatch Patch { get; set; }

        /// <summary>The state the patch moves us to, to be stored once the server accepts it.</summary>
        public LtHashState State { get; set; }
    }

    public sealed class SyncdPatchEncoder
    {
        private readonly IAppStateKeyStore _keys;

        public SyncdPatchEncoder(IAppStateKeyStore keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = keys;
        }

        /// <param name="keyId">
        /// The key we encode with, base64. It is the phone's most recent key, not any of the
        /// older ones we may still hold for reading.
        /// </param>
        public async Task<EncodedAppPatch> EncodeAsync(AppPatchCreate create, string keyId, LtHashState current)
        {
            if (create == null)
            {
                throw new ArgumentNullException(nameof(create));
            }

            if (string.IsNullOrEmpty(keyId))
            {
                throw new InvalidOperationException(
                    "Cannot write app state before the phone has shared a sync key");
            }

            var keyData = await _keys.GetAsync(keyId).ConfigureAwait(false);
            if (keyData == null)
            {
                throw new AppStateKeyMissingException(create.Collection, keyId);
            }

            var keys = AppStateKeys.Expand(keyData);
            var encodedKeyId = Convert.FromBase64String(keyId);
            var state = current != null ? current.Clone() : new LtHashState(create.Collection);

            var index = Encoding.UTF8.GetBytes(BuildIndexJson(create.Index));

            var actionData = new global::Proto.SyncActionData
            {
                Index = ByteString.CopyFrom(index),
                Value = create.SyncAction,
                Padding = ByteString.Empty,
                Version = create.ApiVersion
            };

            var plaintext = actionData.ToByteArray();

            // A fresh IV per mutation, prefixed to the ciphertext exactly as the decoder expects.
            var iv = CryptoUtils.RandomBytes(16);
            var ciphertext = CryptoUtils.AesCbcEncrypt(plaintext, keys.ValueEncryptionKey, iv);
            var content = AppStateKeys.Combine(iv, ciphertext);

            var valueMac = AppStateKeys.GenerateMac(create.IsRemove, content, encodedKeyId, keys.ValueMacKey);
            var indexMac = AppStateKeys.GenerateIndexMac(index, keys.IndexKey);

            var generator = new LtHashGenerator(create.Collection, state);
            generator.Mix(indexMac, valueMac, create.IsRemove);

            var next = generator.Finish(create.Collection, state.Version + 1);

            var snapshotMac = AppStateKeys.GenerateSnapshotMac(
                next.Hash,
                next.Version,
                create.Collection,
                keys.SnapshotMacKey);

            var patchMac = AppStateKeys.GeneratePatchMac(
                snapshotMac,
                new List<byte[]> { valueMac },
                next.Version,
                create.Collection,
                keys.PatchMacKey);

            // No version on the wire. The collection node already quotes the version the patch
            // builds on, and the server rejects the write with bad-request when the patch carries
            // a second, higher one. rc14 leaves the field unset for the same reason and only fills
            // it in on the copy it decodes back for its own event stream.
            var patch = new global::Proto.SyncdPatch
            {
                SnapshotMac = ByteString.CopyFrom(snapshotMac),
                PatchMac = ByteString.CopyFrom(patchMac),
                KeyId = new global::Proto.KeyId { Id = ByteString.CopyFrom(encodedKeyId) }
            };

            patch.Mutations.Add(new global::Proto.SyncdMutation
            {
                Operation = create.IsRemove
                    ? global::Proto.SyncdMutation.Types.SyncdOperation.Remove
                    : global::Proto.SyncdMutation.Types.SyncdOperation.Set,
                Record = new global::Proto.SyncdRecord
                {
                    Index = new global::Proto.SyncdIndex { Blob = ByteString.CopyFrom(indexMac) },
                    Value = new global::Proto.SyncdValue
                    {
                        Blob = ByteString.CopyFrom(AppStateKeys.Combine(content, valueMac))
                    },
                    KeyId = new global::Proto.KeyId { Id = ByteString.CopyFrom(encodedKeyId) }
                }
            });

            return new EncodedAppPatch { Patch = patch, State = next };
        }

        /// <summary>
        /// The index is a JSON array of strings and nothing else, so it is written directly rather
        /// than through a serializer. Quotes and backslashes are escaped for completeness; the
        /// values in practice are action names and JIDs, which contain neither.
        /// </summary>
        private static string BuildIndexJson(IEnumerable<string> parts)
        {
            var builder = new StringBuilder("[");
            var first = true;

            foreach (var part in parts)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append('"');

                foreach (var character in part ?? string.Empty)
                {
                    if (character == '"' || character == '\\')
                    {
                        builder.Append('\\');
                    }

                    builder.Append(character);
                }

                builder.Append('"');
            }

            return builder.Append(']').ToString();
        }
    }
}
