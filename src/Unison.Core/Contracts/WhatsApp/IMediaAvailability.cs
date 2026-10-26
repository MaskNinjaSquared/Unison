using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// On-demand download / hydrate of message media the user opened. WinRT file work and
    /// the download gate stay in the UWP adapter — MessageFacade only awaits this port.
    /// </summary>
    public interface IMediaAvailability
    {
        Task<string> EnsureAudioAsync(ChatMessage message);

        Task<string> EnsureImageAsync(ChatMessage message);

        Task<string> EnsureVideoAsync(ChatMessage message);

        Task<string> EnsureDocumentAsync(ChatMessage message);

        void PrepareLocalPlaybackInBackground(ChatMessage message);

        Task HydrateImageAsync(
            ChatMessage message,
            Message.Types.ImageMessage image,
            string messageId,
            string chatJid);

        Task HydrateStickerAsync(
            ChatMessage message,
            Message.Types.StickerMessage sticker,
            string messageId,
            string chatJid);
    }
}
