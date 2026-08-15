using System.Threading.Tasks;
using Unison.Core.ViewModels;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Platform dialogs. Methods that need form state receive the target ViewModel
    /// (Imgur pattern: ShowCustomApiKeyDialog(SettingsViewModel)).
    /// </summary>
    public interface IDialogService
    {
        Task<bool> ShowConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText);

        Task ShowMessageAsync(string title, string content, string closeButtonText);

        /// <summary>
        /// Simple text prompt. Returns null if the user cancels.
        /// </summary>
        Task<string> ShowInputAsync(
            string title,
            string prompt,
            string placeholder,
            string primaryButtonText,
            string closeButtonText);

        /// <summary>Shows the pairing code for the given login VM context.</summary>
        Task ShowPairingCodeAsync(LoginViewModel loginVm, string code);

        /// <summary>Opens the new-chat dialog bound to the supplied VM. Returns resolved JID or null.</summary>
        Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm);

        /// <summary>
        /// Image send confirmation with preview. Returns true if the user taps Send.
        /// </summary>
        Task<bool> ShowImageSendPreviewAsync(byte[] imageBytes, string infoText);

        /// <summary>Fullscreen pairing QR preview (tap on login QR). No-op if payload empty.</summary>
        Task ShowQrFullscreenAsync(string qrData);
    }
}
