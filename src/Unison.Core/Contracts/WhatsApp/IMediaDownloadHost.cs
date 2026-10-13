using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// The socket read and the quiet save the on-demand media download still asks of the client.
    /// </summary>
    public interface IMediaDownloadHost
    {
        Task<byte[]> DownloadAndDecryptAsync(
            string url,
            string directPath,
            byte[] mediaKey,
            string mediaType,
            byte[] expectedFileEncSha256);

        /// <summary>Writes the row. A store failure is logged and swallowed, as the old save was.</summary>
        Task SaveMessageQuietAsync(string chatJid, ChatMessage message);

        void NotifyMessagesChanged(string chatJid);
    }
}
