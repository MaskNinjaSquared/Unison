using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// System clipboard: copy text out, classify/read paste for the open chat.
    /// </summary>
    public interface ICopyService
    {
        void CopyText(string text);

        /// <summary>
        /// True when the clipboard advertises Bitmap or StorageItems (sync, no byte read).
        /// Used by Ctrl+V to decide whether to intercept before the TextBox pastes text.
        /// </summary>
        bool HasPasteableMedia();

        /// <summary>
        /// Reads clipboard into Image / Audio / Document (video skipped) and optional text.
        /// </summary>
        Task<ClipboardPasteResult> ReadPasteAsync();
    }
}
