using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Unison.Baileys.Crypto;
using Newtonsoft.Json;

using Unison.Baileys.Client;
using Unison.Core.Helpers;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// Persists authentication state to local storage.
    /// Uses ApplicationData.Current.LocalSettings for UWP.
    /// </summary>
    public class AuthStore : IAuthPersistence
    {
        private const string AUTH_STATE_KEY = "auth_state";
        private const string CONTAINER_NAME = "WhatsAppAuth";

        // A LocalSettings value stops at 8192 bytes and throws past it. The keys are fixed-size and
        // come to about a kilobyte, but SenderKeyMemory records every participant device that holds
        // our sender key, so a handful of active groups reaches the ceiling - and the write that
        // overflows it is the group send itself, via CredsUpdate. Losing the memory costs one
        // re-send of the sender key; losing the write costs the account, because a save that throws
        // leaves the credentials behind and the next cold start reads that as "never paired".
        private const string SENDER_KEY_MEMORY_FILE = "sender-key-memory.json";

        private readonly ApplicationDataContainer _settings;

        /// <summary>Creds updates can overlap, and two of them replacing the same file do not.</summary>
        private readonly System.Threading.SemaphoreSlim _senderKeyMemoryGate =
            new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Last memory written to disk, so an unchanged one does not cost a file write per send.</summary>
        private string _lastSenderKeyMemoryJson;

        /// <summary>
        /// Set when a saved account is there but could not be read. The caller answers a null load
        /// by creating an empty <see cref="AuthState"/>, and that empty state must not be written
        /// over credentials we merely failed to parse — a read that fails once would otherwise
        /// unlink the device for good.
        /// </summary>
        private bool _credentialsPresentButUnreadable;

        public AuthStore()
        {
            _settings = ApplicationData.Current.LocalSettings
                .CreateContainer(CONTAINER_NAME, ApplicationDataCreateDisposition.Always);
        }

        /// <summary>
        /// Stores the auth state to local storage
        /// </summary>
        public async Task SaveAsync(AuthState state)
        {
            try
            {
                if (state == null)
                {
                    Debug.WriteLine("[AuthStore] Save skipped: auth state is null");
                    return;
                }

                // A pairing the user just completed is deliberate and replaces whatever is there.
                // Anything else, while the stored credentials are unreadable, is the boot path
                // saving the empty state it had to invent — the one write that loses the account.
                if (_credentialsPresentButUnreadable && !state.Registered)
                {
                    Debug.WriteLine(
                        "[AuthStore] Save skipped: refusing to write an unregistered state over " +
                        "credentials that are present but unreadable");
                    return;
                }

                if (state.NoiseKey?.Private == null ||
                    state.NoiseKey.Public == null ||
                    state.SignedIdentityKey?.Private == null ||
                    state.SignedIdentityKey.Public == null ||
                    state.SignedPreKey?.KeyPair?.Private == null ||
                    state.SignedPreKey.KeyPair.Public == null ||
                    state.SignedPreKey.Signature == null)
                {
                    Debug.WriteLine("[AuthStore] Save skipped: auth state is incomplete");
                    return;
                }

                var dto = new AuthStateDto
                {
                    NoiseKeyPrivate = Convert.ToBase64String(state.NoiseKey.Private),
                    NoiseKeyPublic = Convert.ToBase64String(state.NoiseKey.Public),
                    SignedIdentityKeyPrivate = Convert.ToBase64String(state.SignedIdentityKey.Private),
                    SignedIdentityKeyPublic = Convert.ToBase64String(state.SignedIdentityKey.Public),
                    PairingEphemeralPrivate = state.PairingEphemeralKeyPair?.Private != null ? Convert.ToBase64String(state.PairingEphemeralKeyPair.Private) : null,
                    PairingEphemeralPublic = state.PairingEphemeralKeyPair?.Public != null ? Convert.ToBase64String(state.PairingEphemeralKeyPair.Public) : null,
                    SignedPreKeyId = state.SignedPreKey.KeyId,
                    SignedPreKeyPrivate = Convert.ToBase64String(state.SignedPreKey.KeyPair.Private),
                    SignedPreKeyPublic = Convert.ToBase64String(state.SignedPreKey.KeyPair.Public),
                    SignedPreKeySignature = Convert.ToBase64String(state.SignedPreKey.Signature),
                    RegistrationId = state.RegistrationId,
                    AdvSecretKey = state.AdvSecretKey,
                    PairingCode = state.PairingCode,
                    RoutingInfo = state.RoutingInfo != null ? Convert.ToBase64String(state.RoutingInfo) : null,
                    NextPreKeyId = state.NextPreKeyId,
                    MeId = state.Me?.Id,
                    MeName = state.Me?.Name,
                    MePhone = ResolveMePhone(state.Me?.Phone, state.Me?.Id),
                    MeLid = state.Me?.Lid,
                    MeAvatarUrl = state.Me?.AvatarUrl,
                    Registered = state.Registered,
                    MyAppStateKeyId = state.MyAppStateKeyId,
                    LastAccountSyncTimestamp = state.LastAccountSyncTimestamp
                };

                var json = JsonConvert.SerializeObject(dto);
                _settings.Values[AUTH_STATE_KEY] = json;

                Debug.WriteLine("[AuthStore] Saved auth state");

                // After the credentials, and never in their way: this is a cache, and a phone that
                // dies mid-write should cost a re-send, not the session.
                await SaveSenderKeyMemoryAsync(state.SenderKeyMemory).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthStore] Failed to save: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Loads the auth state from local storage
        /// </summary>
        public async Task<AuthState> LoadAsync()
        {
            string json;
            try
            {
                if (!_settings.Values.ContainsKey(AUTH_STATE_KEY))
                {
                    Debug.WriteLine("[AuthStore] No saved auth state found");
                    _credentialsPresentButUnreadable = false;
                    return null;
                }

                json = _settings.Values[AUTH_STATE_KEY] as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthStore] Failed to reach the settings container: {ex.Message}");
                _credentialsPresentButUnreadable = true;
                return null;
            }

            if (string.IsNullOrEmpty(json))
            {
                _credentialsPresentButUnreadable = false;
                return null;
            }

            try
            {
                var dto = JsonConvert.DeserializeObject<AuthStateDto>(json);
                if (dto == null)
                {
                    Debug.WriteLine("[AuthStore] Saved auth state did not parse into a DTO");
                    _credentialsPresentButUnreadable = true;
                    return null;
                }

                var state = new AuthState
                {
                    NoiseKey = new KeyPair(
                        Convert.FromBase64String(dto.NoiseKeyPrivate),
                        Convert.FromBase64String(dto.NoiseKeyPublic)
                    ),
                    SignedIdentityKey = new KeyPair(
                        Convert.FromBase64String(dto.SignedIdentityKeyPrivate),
                        Convert.FromBase64String(dto.SignedIdentityKeyPublic)
                    ),
                    PairingEphemeralKeyPair = !string.IsNullOrEmpty(dto.PairingEphemeralPrivate) && !string.IsNullOrEmpty(dto.PairingEphemeralPublic)
                        ? new KeyPair(
                            Convert.FromBase64String(dto.PairingEphemeralPrivate),
                            Convert.FromBase64String(dto.PairingEphemeralPublic)
                        )
                        : null,
                    SignedPreKey = new SignedPreKeyData
                    {
                        KeyId = dto.SignedPreKeyId,
                        KeyPair = new KeyPair(
                            Convert.FromBase64String(dto.SignedPreKeyPrivate),
                            Convert.FromBase64String(dto.SignedPreKeyPublic)
                        ),
                        Signature = Convert.FromBase64String(dto.SignedPreKeySignature)
                    },
                    RegistrationId = dto.RegistrationId,
                    AdvSecretKey = dto.AdvSecretKey,
                    PairingCode = dto.PairingCode,
                    RoutingInfo = !string.IsNullOrEmpty(dto.RoutingInfo) 
                        ? Convert.FromBase64String(dto.RoutingInfo) 
                        : null,
                    NextPreKeyId = dto.NextPreKeyId,
                    Registered = dto.Registered,
                    MyAppStateKeyId = dto.MyAppStateKeyId,
                    LastAccountSyncTimestamp = dto.LastAccountSyncTimestamp
                };

                // dto.SenderKeyMemory is what installs written before the split still carry inline.
                state.SenderKeyMemory =
                    await LoadSenderKeyMemoryAsync(dto.SenderKeyMemory).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(dto.MeId))
                {
                    state.Me = new UserInfo
                    {
                        Id = dto.MeId,
                        Name = dto.MeName,
                        Phone = ResolveMePhone(dto.MePhone, dto.MeId),
                        Lid = dto.MeLid,
                        AvatarUrl = dto.MeAvatarUrl
                    };
                }

                Debug.WriteLine($"[AuthStore] Loaded auth state, registered: {state.Registered}");
                _credentialsPresentButUnreadable = false;
                return state;
            }
            catch (Exception ex)
            {
                // There is a saved account here that we could not read. Returning null says "no
                // account" to the caller, which answers it by building a fresh AuthState - so the
                // flag stays up to stop that empty state from being written over the real one.
                Debug.WriteLine($"[AuthStore] Failed to read the saved credentials: {ex.Message}");
                _credentialsPresentButUnreadable = true;
                return null;
            }
        }

        /// <summary>
        /// Clears the stored auth state
        /// </summary>
        public async Task ClearAsync()
        {
            try
            {
                if (_settings.Values.ContainsKey(AUTH_STATE_KEY))
                {
                    _settings.Values.Remove(AUTH_STATE_KEY);
                }

                // Nothing is being protected once the account is gone.
                _credentialsPresentButUnreadable = false;
                _lastSenderKeyMemoryJson = null;

                try
                {
                    var item = await ApplicationData.Current.LocalFolder
                        .TryGetItemAsync(SENDER_KEY_MEMORY_FILE);

                    if (item != null)
                    {
                        await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AuthStore] Could not delete the sender-key memory: {ex.Message}");
                }

                Debug.WriteLine("[AuthStore] Cleared auth state");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthStore] Failed to clear: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if auth state exists
        /// </summary>
        public bool HasSavedState()
        {
            return _settings.Values.ContainsKey(AUTH_STATE_KEY);
        }

        /// <summary>
        /// Reads the sender-key memory from its own file, migrating the copy that older installs
        /// still carry inside the credential blob.
        /// </summary>
        private async Task<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>> LoadSenderKeyMemoryAsync(
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> inlineMemory)
        {
            try
            {
                var item = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync(SENDER_KEY_MEMORY_FILE);

                var file = item as StorageFile;
                if (file != null)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var stored = JsonConvert
                            .DeserializeObject<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>>(json);

                        if (stored != null)
                        {
                            _lastSenderKeyMemoryJson = json;
                            return stored;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Costs a re-send of the sender key, nothing more.
                Debug.WriteLine($"[AuthStore] Could not read the sender-key memory: {ex.Message}");
            }

            if (inlineMemory != null && inlineMemory.Count > 0)
            {
                Debug.WriteLine($"[AuthStore] Migrating {inlineMemory.Count} sender-key memory entries out of the credential blob");
                await SaveSenderKeyMemoryAsync(inlineMemory).ConfigureAwait(false);
                return inlineMemory;
            }

            return new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
        }

        private async Task SaveSenderKeyMemoryAsync(
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> memory)
        {
            await _senderKeyMemoryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = JsonConvert.SerializeObject(
                    memory ?? new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>());

                // Saves run on every creds update, which on a group send means every message.
                if (string.Equals(json, _lastSenderKeyMemoryJson, StringComparison.Ordinal))
                {
                    return;
                }

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    SENDER_KEY_MEMORY_FILE, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(file, json);
                _lastSenderKeyMemoryJson = json;
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: the credentials are already saved by this point, and
                // this record only spares the group a re-send it can afford.
                Debug.WriteLine($"[AuthStore] Could not save the sender-key memory: {ex.Message}");
            }
            finally
            {
                _senderKeyMemoryGate.Release();
            }
        }

        /// <summary>
        /// PN digits for the account. Prefers the stored value; otherwise takes them from the JID.
        /// </summary>
        private static string ResolveMePhone(string storedPhone, string meId)
        {
            if (!string.IsNullOrWhiteSpace(storedPhone))
            {
                return storedPhone.Trim();
            }

            return JidHelper.TryPhoneFromJid(meId);
        }

        /// <summary>
        /// DTO for JSON serialization of auth state
        /// </summary>
        private class AuthStateDto
        {
            public string NoiseKeyPrivate { get; set; }
            public string NoiseKeyPublic { get; set; }
            public string SignedIdentityKeyPrivate { get; set; }
            public string SignedIdentityKeyPublic { get; set; }
            public string PairingEphemeralPrivate { get; set; }
            public string PairingEphemeralPublic { get; set; }
            public int SignedPreKeyId { get; set; }
            public string SignedPreKeyPrivate { get; set; }
            public string SignedPreKeyPublic { get; set; }
            public string SignedPreKeySignature { get; set; }
            public int RegistrationId { get; set; }
            public string AdvSecretKey { get; set; }
            public string PairingCode { get; set; }
            public string RoutingInfo { get; set; }
            public int NextPreKeyId { get; set; }
            public string MeId { get; set; }
            public string MeName { get; set; }
            public string MePhone { get; set; }
            public string MeLid { get; set; }
            public string MeAvatarUrl { get; set; }
            public bool Registered { get; set; }
            public string MyAppStateKeyId { get; set; }
            public long LastAccountSyncTimestamp { get; set; }

            /// <summary>
            /// Read-only leftover: the memory now lives in its own file, but installs written before
            /// the split still have it here and are migrated on the next load. Never written again.
            /// </summary>
            public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> SenderKeyMemory { get; set; }
        }
    }
}
