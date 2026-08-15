using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unison.Baileys.Crypto;
using Windows.Storage;
using Unison.Baileys.Client;

namespace Unison.Background
{
    /// <summary>
    /// Compact, read-only-at-runtime Signal checkpoint prepared by the foreground
    /// immediately before handoff. The task clones it for preview replay and never
    /// writes ratchet changes back to the authoritative SignalKeys store.
    /// </summary>
    internal static class BackgroundSignalSnapshotStore
    {
        private const string FileName =
            "socket-broker-signal-preview-v673b.json";

        private sealed class SnapshotDto
        {
            public int Version { get; set; }
            public DateTime SavedUtc { get; set; }
            public string IdentityPrivate { get; set; }
            public string IdentityPublic { get; set; }
            public int SignedPreKeyId { get; set; }
            public string SignedPreKeyPrivate { get; set; }
            public string SignedPreKeyPublic { get; set; }
            public string SignedPreKeySignature { get; set; }
            public int RegistrationId { get; set; }
            public string MeId { get; set; }
            public string MeName { get; set; }
            public string MeLid { get; set; }
            public bool Registered { get; set; }
            public Dictionary<string, string> Sessions { get; set; }
            public List<PreKeyDto> PreKeys { get; set; }
        }

        private sealed class PreKeyDto
        {
            public int Id { get; set; }
            public string Private { get; set; }
            public string Public { get; set; }
        }

        public static async Task SaveAsync(
            AuthState state,
            IDictionary<string, byte[]> senderKeys)
        {
            if (state?.SignedIdentityKey?.Private == null ||
                state.SignedIdentityKey.Public == null ||
                state.SignedPreKey?.KeyPair?.Private == null ||
                state.SignedPreKey.KeyPair.Public == null)
            {
                throw new InvalidOperationException(
                    "Signal preview state is incomplete");
            }

            var sessions = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in state.Sessions ??
                     new Dictionary<string, byte[]>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) &&
                    pair.Value != null)
                {
                    sessions[pair.Key] = Convert.ToBase64String(pair.Value);
                }
            }
            foreach (var pair in senderKeys ??
                     new Dictionary<string, byte[]>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) &&
                    pair.Value != null)
                {
                    sessions[pair.Key] = Convert.ToBase64String(pair.Value);
                }
            }

            var dto = new SnapshotDto
            {
                Version = 1,
                SavedUtc = DateTime.UtcNow,
                IdentityPrivate =
                    Convert.ToBase64String(state.SignedIdentityKey.Private),
                IdentityPublic =
                    Convert.ToBase64String(state.SignedIdentityKey.Public),
                SignedPreKeyId = state.SignedPreKey.KeyId,
                SignedPreKeyPrivate = Convert.ToBase64String(
                    state.SignedPreKey.KeyPair.Private),
                SignedPreKeyPublic = Convert.ToBase64String(
                    state.SignedPreKey.KeyPair.Public),
                SignedPreKeySignature = state.SignedPreKey.Signature == null
                    ? null
                    : Convert.ToBase64String(state.SignedPreKey.Signature),
                RegistrationId = state.RegistrationId,
                MeId = state.Me?.Id,
                MeName = state.Me?.Name,
                MeLid = state.Me?.Lid,
                Registered = state.Registered,
                Sessions = sessions,
                PreKeys = (state.PreKeys ??
                           new Dictionary<int, PreKeyData>())
                    .Where(pair => pair.Value?.KeyPair?.Private != null &&
                                   pair.Value.KeyPair.Public != null)
                    .Select(pair => new PreKeyDto
                    {
                        Id = pair.Key,
                        Private = Convert.ToBase64String(
                            pair.Value.KeyPair.Private),
                        Public = Convert.ToBase64String(
                            pair.Value.KeyPair.Public)
                    })
                    .ToList()
            };

            string json = JsonConvert.SerializeObject(dto, Formatting.None);
            string temporaryName =
                FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            StorageFile temporary =
                await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    temporaryName,
                    CreationCollisionOption.FailIfExists);
            try
            {
                await FileIO.WriteTextAsync(temporary, json);
                await temporary.RenameAsync(
                    FileName,
                    NameCollisionOption.ReplaceExisting);
                temporary = null;
            }
            finally
            {
                if (temporary != null)
                {
                    try
                    {
                        await temporary.DeleteAsync(
                            StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static async Task<AuthState> LoadCloneAsync()
        {
            try
            {
                StorageFile file =
                    await ApplicationData.Current.LocalFolder.GetFileAsync(
                        FileName);
                string json = await FileIO.ReadTextAsync(file);
                var dto = JsonConvert.DeserializeObject<SnapshotDto>(json);
                if (dto == null ||
                    dto.Version != 1 ||
                    string.IsNullOrWhiteSpace(dto.IdentityPrivate) ||
                    string.IsNullOrWhiteSpace(dto.IdentityPublic) ||
                    string.IsNullOrWhiteSpace(dto.SignedPreKeyPrivate) ||
                    string.IsNullOrWhiteSpace(dto.SignedPreKeyPublic))
                {
                    return null;
                }

                var state = new AuthState
                {
                    SignedIdentityKey = new KeyPair(
                        Convert.FromBase64String(dto.IdentityPrivate),
                        Convert.FromBase64String(dto.IdentityPublic)),
                    SignedPreKey = new SignedPreKeyData
                    {
                        KeyId = dto.SignedPreKeyId,
                        KeyPair = new KeyPair(
                            Convert.FromBase64String(dto.SignedPreKeyPrivate),
                            Convert.FromBase64String(dto.SignedPreKeyPublic)),
                        Signature =
                            string.IsNullOrWhiteSpace(dto.SignedPreKeySignature)
                                ? null
                                : Convert.FromBase64String(
                                    dto.SignedPreKeySignature)
                    },
                    RegistrationId = dto.RegistrationId,
                    Registered = dto.Registered,
                    Me = string.IsNullOrWhiteSpace(dto.MeId) &&
                         string.IsNullOrWhiteSpace(dto.MeLid)
                        ? null
                        : new UserInfo
                        {
                            Id = dto.MeId,
                            Name = dto.MeName,
                            Lid = dto.MeLid
                        },
                    Sessions = new Dictionary<string, byte[]>(
                        StringComparer.OrdinalIgnoreCase),
                    PreKeys = new Dictionary<int, PreKeyData>()
                };

                foreach (var pair in dto.Sessions ??
                         new Dictionary<string, string>())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        state.Sessions[pair.Key] =
                            Convert.FromBase64String(pair.Value);
                    }
                }
                foreach (PreKeyDto preKey in dto.PreKeys ??
                         new List<PreKeyDto>())
                {
                    if (preKey == null ||
                        string.IsNullOrWhiteSpace(preKey.Private) ||
                        string.IsNullOrWhiteSpace(preKey.Public))
                    {
                        continue;
                    }
                    state.PreKeys[preKey.Id] = new PreKeyData
                    {
                        Id = preKey.Id,
                        KeyPair = new KeyPair(
                            Convert.FromBase64String(preKey.Private),
                            Convert.FromBase64String(preKey.Public))
                    };
                }
                return state;
            }
            catch
            {
                return null;
            }
        }

        public static async Task ClearAsync()
        {
            try
            {
                StorageFile file =
                    await ApplicationData.Current.LocalFolder.GetFileAsync(
                        FileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }
    }
}
