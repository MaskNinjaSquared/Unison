using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Quiet persist for on-demand media download (repaired URI on the row).
    /// </summary>
    /// <remarks>
    /// Hydrate used to ask <c>HasLiveSocket</c> here; that check is
    /// <c>IWhatsAppSessionProvider.IsTransportReady</c> on the availability type instead.
    /// Implemented by <c>QuietMediaMessagePersist</c> over
    /// <see cref="IOutgoingMessageHost.PersistLiveAsync"/> — not by the compatibility client.
    /// </remarks>
    public interface IMediaDownloadHost
    {
        /// <summary>Writes the row. A store failure is logged and swallowed, as the old save was.</summary>
        Task SaveMessageQuietAsync(string chatJid, ChatMessage message);
    }
}
