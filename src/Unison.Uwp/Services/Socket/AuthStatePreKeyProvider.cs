// =============================================================================
// AuthStatePreKeyProvider
//
// Mints the one-time prekey a retry receipt hands to the sender, and the batch
// the post-login upload announces.
//
// A key has to exist on our side before it goes out, otherwise the message the
// peer encrypts with it can never be opened - so it is written to the key store
// and the auth state is flagged as dirty before the id is returned.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Socket.Abstractions;
using Unison.Socket.UseCases.Auth;

namespace Unison.Uwp.Services.Socket
{
    public sealed class AuthStatePreKeyProvider : IPreKeyProvider
    {
        private readonly AuthState _authState;
        private readonly IKeyStore _keyStore;
        private readonly Action _onAuthStateChanged;

        /// <param name="onAuthStateChanged">
        /// Raised after the next prekey id or the upload watermark moves, so the host persists
        /// it. Without this a restart reissues ids that are already in use.
        /// </param>
        public AuthStatePreKeyProvider(AuthState authState, IKeyStore keyStore, Action onAuthStateChanged = null)
        {
            if (authState == null)
            {
                throw new ArgumentNullException(nameof(authState));
            }

            _authState = authState;
            _keyStore = keyStore;
            _onAuthStateChanged = onAuthStateChanged;
        }

        public async Task<PreKeyRecord> GetNextPreKeyAsync()
        {
            var keyId = Interlocked.Increment(ref _authState.NextPreKeyId);
            var preKey = PreKeyData.Generate(keyId);

            _authState.PreKeys[keyId] = preKey;

            if (_keyStore != null)
            {
                await _keyStore.SetPreKeyAsync(keyId, preKey).ConfigureAwait(false);
            }

            _onAuthStateChanged?.Invoke();

            return new PreKeyRecord { KeyId = keyId, PublicKey = preKey.KeyPair.Public };
        }

        public async Task<PreKeyUploadBatch> PrepareUploadBatchAsync(int count)
        {
            Dictionary<int, PreKeyData> stored = _keyStore != null
                ? await _keyStore.GetAllPreKeysAsync().ConfigureAwait(false)
                : new Dictionary<int, PreKeyData>(_authState.PreKeys);

            PreKeyUploadPlan plan = PreKeyUploadPlanDecision.Plan(
                _authState.FirstUnuploadedPreKeyId,
                Volatile.Read(ref _authState.NextPreKeyId),
                count,
                stored.Keys);

            var records = new List<PreKeyRecord>(plan.ReuseIds.Count + plan.MintCount);
            foreach (int id in plan.ReuseIds)
            {
                records.Add(new PreKeyRecord { KeyId = id, PublicKey = stored[id].KeyPair.Public });
            }

            if (plan.MintCount > 0)
            {
                // Reserve the range first: a retry receipt minting meanwhile must land above it.
                AdvanceNextPreKeyId(plan.FirstNewId - 1);
                int lastId = Interlocked.Add(ref _authState.NextPreKeyId, plan.MintCount);
                int firstId = lastId - plan.MintCount + 1;

                var minted = new List<PreKeyData>(plan.MintCount);
                for (int id = firstId; id <= lastId; id++)
                {
                    PreKeyData preKey = PreKeyData.Generate(id);
                    _authState.PreKeys[id] = preKey;
                    minted.Add(preKey);
                    records.Add(new PreKeyRecord { KeyId = id, PublicKey = preKey.KeyPair.Public });
                }

                if (_keyStore != null)
                {
                    await _keyStore.SetPreKeysAsync(minted).ConfigureAwait(false);
                }

                _onAuthStateChanged?.Invoke();
            }

            return new PreKeyUploadBatch(records, plan.ReuseIds.Count, plan.MintCount);
        }

        public void ConfirmUploaded(int highestKeyId)
        {
            int next = PreKeyUploadPlanDecision.FirstUnuploadedAfterAck(
                _authState.FirstUnuploadedPreKeyId,
                highestKeyId);
            if (next == _authState.FirstUnuploadedPreKeyId)
            {
                return;
            }

            _authState.FirstUnuploadedPreKeyId = next;
            _onAuthStateChanged?.Invoke();
        }

        /// <summary>A retry receipt may have bumped the counter meanwhile; never move it back.</summary>
        private void AdvanceNextPreKeyId(int lastIssuedId)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _authState.NextPreKeyId);
                if (current >= lastIssuedId)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _authState.NextPreKeyId, lastIssuedId, current) != current);
        }
    }
}
