using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Exports a cached chat audio file to a user-chosen path (MP3 when possible).
    /// </summary>
    public interface IAudioExportService
    {
        /// <summary>
        /// Decodes WhatsApp Ogg/Opus via Concentus when needed, re-encodes to MP3, then
        /// opens a save picker. Returns the destination path, or null if cancelled/failed.
        /// </summary>
        Task<string> SaveAsMp3Async(string sourceUriOrPath, string suggestedFileName);
    }
}
