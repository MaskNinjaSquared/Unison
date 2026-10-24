// =============================================================================
// IFindPersistedChatMessageHost
//
// Side effects for FindPersistedChatMessageDispatch. History row mapping stays
// on the host.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFindPersistedChatMessageHost
    {
        bool HasHistoryMessageStore { get; }

        IJidResolver JidResolver { get; }

        Task<object> GetHistoryMessageRowAsync(string storeKey, string messageId);

        ChatMessage MapHistoryRowToChatMessage(object row);
    }
}
