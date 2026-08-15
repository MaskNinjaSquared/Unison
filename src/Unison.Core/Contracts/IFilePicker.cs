using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Platform file pickers without WinRT types leaking into Core.
    /// </summary>
    public interface IFilePicker
    {
        /// <summary>Returns local file path, or null if cancelled.</summary>
        Task<string> PickOpenImagePathAsync();

        /// <summary>Returns local file path where text was saved, or null if cancelled.</summary>
        Task<string> PickSaveTextFileAsync(string suggestedFileName, string content);

        /// <summary>
        /// Chat composer attach: image (optimized JPEG/PNG bytes) or audio file.
        /// Returns null if the user cancels.
        /// </summary>
        Task<PickedChatMedia> PickChatAttachmentAsync();

        /// <summary>
        /// Chat composer attach, pictures only (optimized JPEG/PNG bytes).
        /// Returns null if the user cancels.
        /// </summary>
        Task<PickedChatMedia> PickChatImageAsync();

        /// <summary>
        /// Chat composer attach, audio only. Returns null if the user cancels.
        /// </summary>
        Task<PickedChatMedia> PickChatAudioAsync();

        /// <summary>
        /// Saves a local cached image (<c>ms-appdata</c> / path) via FileSavePicker.
        /// Returns the destination path, or null if cancelled.
        /// </summary>
        Task<string> PickSaveLocalImageAsync(string sourceUriOrPath, string suggestedFileName);

        /// <summary>
        /// Saves any local cached file (document/audio/…) via FileSavePicker.
        /// Returns the destination path, or null if cancelled.
        /// </summary>
        Task<string> PickSaveLocalFileAsync(
            string sourceUriOrPath,
            string suggestedFileName,
            string mimeType = null);
    }
}
