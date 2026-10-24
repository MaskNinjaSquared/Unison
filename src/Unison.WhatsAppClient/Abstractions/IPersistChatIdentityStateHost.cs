// =============================================================================
// IPersistChatIdentityStateHost
//
// Side effects for PersistChatIdentityStateDispatch.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Abstractions
{
    public sealed class ChatIdentityCapture
    {
        public ChatCatalogSnapshot Catalog { get; set; }

        public IReadOnlyDictionary<string, string> ContactNames { get; set; }

        public IReadOnlyDictionary<string, string> PhoneNames { get; set; }

        public IReadOnlyDictionary<string, string> Aliases { get; set; }
    }

    public interface IPersistChatIdentityStateHost
    {
        Task<ChatIdentityCapture> CaptureChatIdentitySnapshotsAsync();

        Task PersistChatCatalogAsync(ChatCatalogSnapshot catalog);

        Task SaveContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids);

        Task SavePhoneContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids);

        Task SaveJidAliasesAsync(
            IReadOnlyDictionary<string, string> aliases,
            IReadOnlyList<string> chatJids);

        void LogPersistChatIdentity(string message);
    }
}
