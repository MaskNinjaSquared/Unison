using System.Threading.Tasks;
using Unison.Core.ViewModels;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Platform dialogs. Methods that need form state receive the target ViewModel
    /// (Imgur pattern: ShowCustomApiKeyDialog(SettingsViewModel)).
    /// </summary>
    public enum ConfirmPromptResult
    {
        Primary,
        Cancel,
        Dismissed
    }

    public interface IDialogService
    {
        Task<bool> ShowConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText);

        /// <summary>
        /// Same buttons as <see cref="ShowConfirmAsync"/>, but leaving the app
        /// (Back / suspend) is <see cref="ConfirmPromptResult.Dismissed"/>, not cancel.
        /// </summary>
        Task<ConfirmPromptResult> ShowConfirmResultAsync(
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

        /// <summary>
        /// Fullscreen pairing QR preview (tap on login QR). Subscribes to
        /// <see cref="LoginViewModel.QrFullscreenDismissRequested"/> so the dialog closes when
        /// pairing succeeds. No-op if payload empty.
        /// </summary>
        Task ShowQrFullscreenAsync(LoginViewModel loginVm);

        /// <summary>Fullscreen QR preview from a raw payload (debug / socket slice). No dismiss listener.</summary>
        Task ShowQrFullscreenAsync(string qrData);

        /// <summary>
        /// WhatsApp-shell settings overlay (SplitView sections). Unison keeps the full-page SettingsView.
        /// </summary>
        Task ShowWhatsAppSettingsAsync(SettingsViewModel settingsVm);

        /// <summary>
        /// Read-only list of who reacted to <paramref name="messageVm"/>. No-op when the bubble has
        /// no reactions.
        /// </summary>
        Task ShowReactionsDialogAsync(ChatMessageViewModel messageVm);

        /// <summary>
        /// Mobile-friendly runtime health dialog (snapshot + recent journal + save to disk).
        /// </summary>
        Task ShowRuntimeDiagnosticsAsync();
    }
}
