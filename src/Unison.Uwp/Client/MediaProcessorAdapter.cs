using System.Threading.Tasks;
using Unison.Baileys.Client;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// Adapts MediaUtils to the Baileys IMediaProcessor contract.
    /// </summary>
    public sealed class MediaProcessorAdapter : IMediaProcessor
    {
        public async Task<EncryptedMediaResult> EncryptMediaAsync(byte[] fileBytes, string mediaType)
        {
            var native = await MediaUtils.EncryptMediaAsync(fileBytes, mediaType);
            return new EncryptedMediaResult
            {
                MediaKey = native.MediaKey,
                EncryptedBytes = native.EncryptedBytes,
                Mac = native.Mac,
                FileSha256 = native.FileSha256,
                FileEncSha256 = native.FileEncSha256,
                FileLength = native.FileLength
            };
        }
    }
}
