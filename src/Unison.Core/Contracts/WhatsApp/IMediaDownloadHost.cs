using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// The quiet save the on-demand media download still asks of the client.
    /// </summary>
    public interface IMediaDownloadHost
    {
        /// <summary>Writes the row. A store failure is logged and swallowed, as the old save was.</summary>
        Task SaveMessageQuietAsync(string chatJid, ChatMessage message);

        void NotifyMessagesChanged(string chatJid);

        /// <summary>True when the live socket exists. A missing socket used to skip hydrate entirely.</summary>
        bool HasLiveSocket { get; }
    }
}
