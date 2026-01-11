using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.UWPApp.Client
{
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

        // Account info (ADVSignedDeviceIdentity for device-identity node)
        Task<AccountInfo> GetAccountAsync();
        Task SetAccountAsync(AccountInfo account);

        // Initialization
        Task InitializeAsync();
    }
}
