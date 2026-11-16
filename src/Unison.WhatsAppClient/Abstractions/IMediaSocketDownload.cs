using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// On-demand image/sticker bytes via the socket media stack (CDN + optional phone re-upload).
    /// Returns null when the media module is not attached — caller falls back to plain HTTP.
    /// </summary>
    public interface IMediaSocketDownload
    {
        /// <param name="allowPhoneRetry">
        /// False for channels (@newsletter): phone media-retry is not meaningful for channel media.
        /// </param>
        Task<byte[]> TryDownloadImageOrStickerAsync(
            ChatMessage message,
            bool isSticker,
            bool allowPhoneRetry);
    }
}
