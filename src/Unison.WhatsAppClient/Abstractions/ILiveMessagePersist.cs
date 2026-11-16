// =============================================================================
// ILiveMessagePersist
//
// Durable live upsert for a single bubble (reactions, pins, consolidations).
// Send flows still go through IOutgoingMessageHost; this port keeps MessageFacade
// off the outgoing host for timeline repair writes.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ILiveMessagePersist
    {
        Task PersistLiveAsync(string chatJid, ChatMessage message);
    }
}
