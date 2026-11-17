using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Chats
{
    /// <summary>Client-owned chat pin / favourite surface.</summary>
    public interface IChatFlagsIo
    {
        Task SetPinnedAsync(SetChatPinnedRequest request);

        Task SetFavoriteAsync(SetChatFavoriteRequest request);

        Task MarkReadAsync(MarkChatReadRequest request);
    }
}
