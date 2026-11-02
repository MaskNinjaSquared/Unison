// =============================================================================
// ILiveMessagePersist
//
// Durable live upsert for a single bubble (reactions, pins, consolidations).
// Send flows still go through IOutgoingMessageHost; this port keeps MessageFacade
// off the outgoing host for timeline repair writes.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ILiveMessagePersist
    {
        Task PersistLiveAsync(string chatJid, ChatMessage message);
    }
}
