using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Client-owned media-send surface. Core <c>IMessageService</c> adapts to this.
    /// </summary>
    public interface IMessageMediaIo
    {
        Task<ChatMessageDto> SendImageAsync(SendImageRequest request);

        Task<IReadOnlyList<ChatMessageDto>> SendImagesAsync(SendImagesRequest request);

        Task<ChatMessageDto> SendAudioAsync(SendAudioRequest request);

        Task<ChatMessageDto> SendDocumentAsync(SendDocumentRequest request);
    }
}
