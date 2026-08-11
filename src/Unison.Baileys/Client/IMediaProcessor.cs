using System.Threading.Tasks;

namespace Unison.Baileys.Client
{
    /// <summary>
    /// Abstracts media encryption for WhatsApp uploads.
    /// </summary>
    public interface IMediaProcessor
    {
        Task<EncryptedMediaResult> EncryptMediaAsync(byte[] fileBytes, string mediaType);
    }
}
