// =============================================================================
// AuthStatePreKeyProvider
//
// Mints the one-time prekey a retry receipt hands to the sender.
//
// The key has to exist on our side before it goes out, otherwise the message the
// peer encrypts with it can never be opened - so it is written to the key store
// and the auth state is flagged as dirty before the id is returned.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Socket.Abstractions;

namespace Unison.Uwp.Services.Socket
{
    public sealed class AuthStatePreKeyProvider : IPreKeyProvider
    {
        private readonly AuthState _authState;
        private readonly IKeyStore _keyStore;
        private readonly Action _onAuthStateChanged;

        /// <param name="onAuthStateChanged">
        /// Raised after the next prekey id moves, so the host persists it. Without this a
        /// restart reissues ids that are already in use.
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

            if (_onAuthStateChanged != null)
            {
                _onAuthStateChanged();
            }

            return new PreKeyRecord { KeyId = keyId, PublicKey = preKey.KeyPair.Public };
        }
    }
}
