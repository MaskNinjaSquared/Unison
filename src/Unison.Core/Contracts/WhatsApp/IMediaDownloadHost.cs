using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// The quiet save the on-demand media download still asks of the client.
    /// </summary>
    /// <remarks>
    /// Hydrate used to ask <c>HasLiveSocket</c> here; that check is
    /// <c>IWhatsAppSessionProvider.IsTransportReady</c> on the availability type instead.
    /// Quiet save stays until the persist queue leaves the client.
    /// </remarks>
    public interface IMediaDownloadHost
    {
        /// <summary>Writes the row. A store failure is logged and swallowed, as the old save was.</summary>
        Task SaveMessageQuietAsync(string chatJid, ChatMessage message);
    }
}
