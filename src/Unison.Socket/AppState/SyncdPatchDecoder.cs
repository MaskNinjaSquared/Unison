// =============================================================================
// SyncdPatchDecoder
//
// Turns the encrypted blobs the server sends into readable mutations, and moves
// the collection's hash along as it goes.
//
// Every mutation is checked twice before it is believed: the value MAC proves
// the ciphertext was not altered, and the index MAC proves the decrypted action
// really belongs to the index it was filed under. A failure throws rather than
// skipping, because a collection that has quietly diverged from the phone is
// worse than one that is known to be broken - the caller can resync from zero,
// but only if it is told.
//
// Large patches do not travel inline. When the server points at an external blob
// the mutations are downloaded and take the place of the inline list, which is
// why a patch can arrive apparently empty.
//
// Ports: rc14 decodeSyncdMutations, decodeSyncdPatch and decodeSyncdSnapshot in
// src/Utils/chat-utils.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Socket.Abstractions;

namespace Unison.Socket.AppState
{
    /// <summary>One decoded change: what happened, to which index, carrying which action.</summary>
    public sealed class ChatMutation
    {
        public ChatMutation()
        {
            Index = new List<string>();
        }

        /// <summary>
        /// The index, already split. The first element names the action - "mute", "pin_v1",
        /// "contact" - and the rest identify what it applies to.
        /// </summary>
        public IList<string> Index { get; private set; }

        public global::Proto.SyncActionData SyncAction { get; set; }

        public bool IsRemove { get; set; }

        /// <summary>The action name, or an empty string for a malformed index.</summary>
        public string Action
        {
            get { return Index.Count > 0 ? Index[0] : string.Empty; }
        }
    }

    public sealed class AppStateDecodeResult
    {
        public AppStateDecodeResult()
        {
            Mutations = new List<ChatMutation>();
        }

        public LtHashState State { get; set; }

        public IList<ChatMutation> Mutations { get; private set; }
    }

    public sealed class SyncdPatchDecoder
    {
        private const string AppStateMediaType = "md-app-state";

        private readonly IAppStateKeyStore _keys;
        private readonly IEncryptedMediaDownloader _media;
        private readonly ISocketLog _log;

        public SyncdPatchDecoder(
            IAppStateKeyStore keys,
            IEncryptedMediaDownloader media = null,
            ISocketLog log = null)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }

            _keys = keys;
            _media = media;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Rebuilds a collection from a snapshot, discarding whatever we had. Snapshots are the
        /// server's answer to a companion that has fallen too far behind to be patched forward.
        /// </summary>
        /// <param name="minimumVersion">
        /// Mutations at or below this version are applied to the hash but not reported. Without it
        /// a first sync would announce every mute and every archive the account has ever set as if
        /// it had just happened.
        /// </param>
        public async Task<AppStateDecodeResult> DecodeSnapshotAsync(
            string name,
            global::Proto.SyncdSnapshot snapshot,
            long? minimumVersion)
        {
            if (snapshot == null)
            {
                return new AppStateDecodeResult { State = new LtHashState(name) };
            }

            var version = snapshot.Version != null ? unchecked((long)snapshot.Version.Version) : 0;
            var collectMutations = !minimumVersion.HasValue || version > minimumVersion.Value;
            var generator = new LtHashGenerator(name, new LtHashState(name));
            var result = new AppStateDecodeResult();

            foreach (var record in snapshot.Records)
            {
                await ApplyRecordAsync(name, record, false, generator, result, collectMutations)
                    .ConfigureAwait(false);
            }

            result.State = generator.Finish(name, version);

            await VerifySnapshotMacAsync(name, snapshot.Mac, snapshot.KeyId, result.State, version)
                .ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Applies patches in order on top of an existing state. Each one is verified against the
        /// state it produces, so a patch applied out of order fails here rather than silently.
        /// </summary>
        /// <param name="minimumVersion">
        /// Patches at or below this version are applied but not reported. See
        /// <see cref="DecodeSnapshotAsync"/>.
        /// </param>
        public async Task<AppStateDecodeResult> DecodePatchesAsync(
            string name,
            IEnumerable<global::Proto.SyncdPatch> patches,
            LtHashState initial,
            long? minimumVersion)
        {
            var result = new AppStateDecodeResult();
            var current = initial != null ? initial.Clone() : new LtHashState(name);

            if (patches == null)
            {
                result.State = current;
                return result;
            }

            foreach (var patch in patches)
            {
                if (patch == null)
                {
                    continue;
                }

                var mutations = await ResolveMutationsAsync(patch).ConfigureAwait(false);
                var version = patch.Version != null ? unchecked((long)patch.Version.Version) : current.Version;

                await VerifyPatchMacAsync(name, patch, mutations, version).ConfigureAwait(false);

                var collectMutations = !minimumVersion.HasValue || version > minimumVersion.Value;
                var generator = new LtHashGenerator(name, current);

                foreach (var mutation in mutations)
                {
                    if (mutation == null || mutation.Record == null)
                    {
                        continue;
                    }

                    var isRemove = mutation.Operation == global::Proto.SyncdMutation.Types.SyncdOperation.Remove;
                    await ApplyRecordAsync(name, mutation.Record, isRemove, generator, result, collectMutations)
                        .ConfigureAwait(false);
                }

                current = generator.Finish(name, version);

                if (generator.SkippedRemovals > 0)
                {
                    _log.Warn(
                        "[AppState] " + name + " v" + version + ": skipped " + generator.SkippedRemovals +
                        " removal(s) with no known previous value");
                }

                await VerifySnapshotMacAsync(name, patch.SnapshotMac, patch.KeyId, current, version)
                    .ConfigureAwait(false);
            }

            result.State = current;
            return result;
        }

        /// <summary>
        /// Decrypts one record, proves it belongs where it was filed, and mixes it into the hash.
        /// </summary>
        private async Task ApplyRecordAsync(
            string name,
            global::Proto.SyncdRecord record,
            bool isRemove,
            LtHashGenerator generator,
            AppStateDecodeResult result,
            bool collectMutations)
        {
            if (record == null || record.Value == null || record.Value.Blob == null)
            {
                return;
            }

            var blob = record.Value.Blob.ToByteArray();
            if (blob.Length < 48)
            {
                throw new InvalidOperationException(
                    "App-state mutation for " + name + " is too short to hold a MAC and an IV");
            }

            var content = AppStateKeys.Slice(blob, 0, blob.Length - 32);
            var valueMac = AppStateKeys.Slice(blob, blob.Length - 32, 32);

            var keyId = record.KeyId != null && record.KeyId.Id != null
                ? record.KeyId.Id.ToByteArray()
                : new byte[0];

            var keys = await GetKeysAsync(name, keyId).ConfigureAwait(false);

            var expectedValueMac = AppStateKeys.GenerateMac(isRemove, content, keyId, keys.ValueMacKey);
            if (!AppStateKeys.ConstantTimeEquals(expectedValueMac, valueMac))
            {
                throw new InvalidOperationException("App-state value MAC mismatch in " + name);
            }

            var iv = AppStateKeys.Slice(content, 0, 16);
            var cipher = AppStateKeys.Slice(content, 16, content.Length - 16);
            var plaintext = Unison.Baileys.Crypto.CryptoUtils.AesCbcDecrypt(cipher, keys.ValueEncryptionKey, iv);

            var syncAction = global::Proto.SyncActionData.Parser.ParseFrom(plaintext);
            var indexBytes = syncAction.Index != null ? syncAction.Index.ToByteArray() : new byte[0];

            var recordIndexMac = record.Index != null && record.Index.Blob != null
                ? record.Index.Blob.ToByteArray()
                : new byte[0];

            var expectedIndexMac = AppStateKeys.GenerateIndexMac(indexBytes, keys.IndexKey);
            if (!AppStateKeys.ConstantTimeEquals(expectedIndexMac, recordIndexMac))
            {
                throw new InvalidOperationException("App-state index MAC mismatch in " + name);
            }

            generator.Mix(recordIndexMac, valueMac, isRemove);

            if (!collectMutations)
            {
                return;
            }

            var chatMutation = new ChatMutation
            {
                SyncAction = syncAction,
                IsRemove = isRemove
            };

            foreach (var part in AppStateIndex.Parse(indexBytes))
            {
                chatMutation.Index.Add(part);
            }

            result.Mutations.Add(chatMutation);
        }

        /// <summary>
        /// Patches too large to inline arrive as a reference to an encrypted blob, which holds the
        /// same mutations in the same shape.
        /// </summary>
        private async Task<IList<global::Proto.SyncdMutation>> ResolveMutationsAsync(
            global::Proto.SyncdPatch patch)
        {
            var external = patch.ExternalMutations;
            if (external == null || string.IsNullOrEmpty(external.DirectPath))
            {
                return new List<global::Proto.SyncdMutation>(patch.Mutations);
            }

            if (_media == null)
            {
                throw new InvalidOperationException(
                    "An app-state patch is stored externally but no media downloader was supplied");
            }

            var data = await _media.DownloadAsync(new EncryptedMediaRequest
            {
                DirectPath = external.DirectPath,
                MediaKey = external.MediaKey != null ? external.MediaKey.ToByteArray() : null,
                MediaType = AppStateMediaType,
                ExpectedLength = unchecked((long)external.FileSizeBytes)
            }).ConfigureAwait(false);

            var mutations = global::Proto.SyncdMutations.Parser.ParseFrom(data);
            return mutations != null
                ? new List<global::Proto.SyncdMutation>(mutations.Mutations)
                : new List<global::Proto.SyncdMutation>();
        }

        /// <summary>Downloads a snapshot, which is always stored externally.</summary>
        public async Task<global::Proto.SyncdSnapshot> DownloadSnapshotAsync(
            global::Proto.ExternalBlobReference reference)
        {
            if (reference == null || string.IsNullOrEmpty(reference.DirectPath))
            {
                return null;
            }

            if (_media == null)
            {
                throw new InvalidOperationException(
                    "An app-state snapshot is available but no media downloader was supplied");
            }

            var data = await _media.DownloadAsync(new EncryptedMediaRequest
            {
                DirectPath = reference.DirectPath,
                MediaKey = reference.MediaKey != null ? reference.MediaKey.ToByteArray() : null,
                MediaType = AppStateMediaType,
                ExpectedLength = unchecked((long)reference.FileSizeBytes)
            }).ConfigureAwait(false);

            return global::Proto.SyncdSnapshot.Parser.ParseFrom(data);
        }

        private async Task VerifySnapshotMacAsync(
            string name,
            ByteString mac,
            global::Proto.KeyId keyId,
            LtHashState state,
            long version)
        {
            if (mac == null || keyId == null || keyId.Id == null)
            {
                return;
            }

            var keys = await GetKeysAsync(name, keyId.Id.ToByteArray()).ConfigureAwait(false);
            var expected = AppStateKeys.GenerateSnapshotMac(state.Hash, version, name, keys.SnapshotMacKey);

            if (!AppStateKeys.ConstantTimeEquals(expected, mac.ToByteArray()))
            {
                throw new InvalidOperationException(
                    "App-state snapshot MAC mismatch in " + name + " at v" + version);
            }
        }

        private async Task VerifyPatchMacAsync(
            string name,
            global::Proto.SyncdPatch patch,
            IEnumerable<global::Proto.SyncdMutation> mutations,
            long version)
        {
            if (patch.PatchMac == null || patch.KeyId == null || patch.KeyId.Id == null)
            {
                return;
            }

            var valueMacs = new List<byte[]>();

            foreach (var mutation in mutations)
            {
                if (mutation == null || mutation.Record == null ||
                    mutation.Record.Value == null || mutation.Record.Value.Blob == null)
                {
                    continue;
                }

                var blob = mutation.Record.Value.Blob.ToByteArray();
                if (blob.Length >= 32)
                {
                    valueMacs.Add(AppStateKeys.Slice(blob, blob.Length - 32, 32));
                }
            }

            var keys = await GetKeysAsync(name, patch.KeyId.Id.ToByteArray()).ConfigureAwait(false);
            var snapshotMac = patch.SnapshotMac != null ? patch.SnapshotMac.ToByteArray() : new byte[0];
            var expected = AppStateKeys.GeneratePatchMac(snapshotMac, valueMacs, version, name, keys.PatchMacKey);

            if (!AppStateKeys.ConstantTimeEquals(expected, patch.PatchMac.ToByteArray()))
            {
                throw new InvalidOperationException(
                    "App-state patch MAC mismatch in " + name + " at v" + version);
            }
        }

        private async Task<MutationKeys> GetKeysAsync(string name, byte[] keyId)
        {
            var id = Convert.ToBase64String(keyId ?? new byte[0]);
            var keyData = await _keys.GetAsync(id).ConfigureAwait(false);

            if (keyData == null)
            {
                throw new AppStateKeyMissingException(name, id);
            }

            return AppStateKeys.Expand(keyData);
        }
    }

    /// <summary>
    /// Thrown when a collection cannot be read because the phone has not shared the key it was
    /// encrypted with. It is a distinct type because the answer is to ask and wait, not to resync.
    /// </summary>
    public sealed class AppStateKeyMissingException : Exception
    {
        public AppStateKeyMissingException(string collection, string keyId)
            : base("Missing app-state sync key " + keyId + " for " + collection)
        {
            Collection = collection;
            KeyId = keyId;
        }

        public string Collection { get; private set; }

        public string KeyId { get; private set; }
    }
}
