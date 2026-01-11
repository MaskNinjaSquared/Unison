using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Unison.UWPApp.Crypto;
using Newtonsoft.Json;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Persists authentication state to local storage.
    /// Uses ApplicationData.Current.LocalSettings for UWP.
    /// </summary>
    public class AuthStore
    {
        private const string AUTH_STATE_KEY = "auth_state";
        private const string CONTAINER_NAME = "WhatsAppAuth";
        
        private readonly ApplicationDataContainer _settings;

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
                var dto = new AuthStateDto
                {
                    NoiseKeyPrivate = Convert.ToBase64String(state.NoiseKey.Private),
                    NoiseKeyPublic = Convert.ToBase64String(state.NoiseKey.Public),
                    SignedIdentityKeyPrivate = Convert.ToBase64String(state.SignedIdentityKey.Private),
                    SignedIdentityKeyPublic = Convert.ToBase64String(state.SignedIdentityKey.Public),
                    PairingEphemeralPrivate = Convert.ToBase64String(state.PairingEphemeralKeyPair.Private),
                    PairingEphemeralPublic = Convert.ToBase64String(state.PairingEphemeralKeyPair.Public),
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
                    MeLid = state.Me?.Lid,
                    Registered = state.Registered
                };

                var json = JsonConvert.SerializeObject(dto);
                _settings.Values[AUTH_STATE_KEY] = json;
                
                Debug.WriteLine("[AuthStore] Saved auth state");
                await Task.CompletedTask;
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
            try
            {
                if (!_settings.Values.ContainsKey(AUTH_STATE_KEY))
                {
                    Debug.WriteLine("[AuthStore] No saved auth state found");
                    return null;
                }

                var json = _settings.Values[AUTH_STATE_KEY] as string;
                if (string.IsNullOrEmpty(json))
                    return null;

                var dto = JsonConvert.DeserializeObject<AuthStateDto>(json);
                if (dto == null)
                    return null;

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
                    PairingEphemeralKeyPair = new KeyPair(
                        Convert.FromBase64String(dto.PairingEphemeralPrivate),
                        Convert.FromBase64String(dto.PairingEphemeralPublic)
                    ),
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
                    Registered = dto.Registered
                };

                if (!string.IsNullOrEmpty(dto.MeId))
                {
                    state.Me = new UserInfo
                    {
                        Id = dto.MeId,
                        Name = dto.MeName,
                        Lid = dto.MeLid
                    };
                }

                Debug.WriteLine($"[AuthStore] Loaded auth state, registered: {state.Registered}");
                await Task.CompletedTask;
                return state;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthStore] Failed to load: {ex.Message}");
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
                Debug.WriteLine("[AuthStore] Cleared auth state");
                await Task.CompletedTask;
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
            public string MeLid { get; set; }
            public bool Registered { get; set; }
        }
    }
}
