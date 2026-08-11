using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;

namespace Unison.Baileys.Client
{
    public class TcTokenData
    {
        public byte[] Token { get; set; }
        public long? Timestamp { get; set; }
        public long? SenderTimestamp { get; set; }
    }

    /// <summary>
    /// Interface for Signal key storage operations.
    /// Abstracts session, pre-key, sender-key, and account persistence.
    /// </summary>
    public interface IKeyStore
    {
        // Sessions
        Task<byte[]> GetSessionAsync(string jid);
        Task SetSessionAsync(string jid, byte[] data);
        Task RemoveSessionAsync(string jid);
        Task<IEnumerable<string>> GetAllSessionJidsAsync();
        bool HasSession(string jid);

        // Pre-keys
        Task<PreKeyData> GetPreKeyAsync(int id);
        Task SetPreKeyAsync(int id, PreKeyData data);
        Task RemovePreKeyAsync(int id);
        Task<Dictionary<int, PreKeyData>> GetAllPreKeysAsync();

        // Sender keys (for groups)
        Task<byte[]> GetSenderKeyAsync(string groupJid, string senderJid);
        Task SetSenderKeyAsync(string groupJid, string senderJid, byte[] data);
        Task<Dictionary<string, byte[]>> GetAllSenderKeysAsync();

        // Trusted-contact privacy tokens (rc10 tctoken lifecycle)
        Task<TcTokenData> GetTcTokenAsync(string jid);
        Task SetTcTokenAsync(string jid, TcTokenData data);
        Task RemoveTcTokenAsync(string jid);
        Task<Dictionary<string, TcTokenData>> GetAllTcTokensAsync();

        // Account info (ADVSignedDeviceIdentity for device-identity node)
        Task<AccountInfo> GetAccountAsync();
        Task SetAccountAsync(AccountInfo account);

        // App-state sync
        Task<Message.Types.AppStateSyncKeyData> GetAppStateSyncKeyAsync(string keyId);
        Task SetAppStateSyncKeyAsync(string keyId, Message.Types.AppStateSyncKeyData data);
        Task<Dictionary<string, Message.Types.AppStateSyncKeyData>> GetAllAppStateSyncKeysAsync();
        Task<AppStateCollectionState> GetAppStateCollectionStateAsync(string name);
        Task SetAppStateCollectionStateAsync(string name, AppStateCollectionState state);
        Task RemoveAppStateCollectionStateAsync(string name);

        // Initialization
        Task InitializeAsync();
        Task ClearAllAsync();
    }
}
