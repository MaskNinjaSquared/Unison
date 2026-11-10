using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
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

    /// <summary>Result of the message-delete confirmation (for me / for everyone / cancel).</summary>
    public enum MessageDeleteChoice
    {
        Cancel,
        ForMe,
        ForEveryone
    }

    public interface IDialogService
    {
        Task<bool> ShowConfirmAsync(
            string title,
            string content,
            string primaryButtonText,
            string closeButtonText);

        /// <summary>
        /// WhatsApp-style delete prompt. When <paramref name="offerDeleteForEveryone"/> is false,
        /// only "delete for me" and cancel are shown.
        /// </summary>
        Task<MessageDeleteChoice> ShowMessageDeleteAsync(
            string content,
            bool offerDeleteForEveryone,
            string deleteForMeText,
            string deleteForEveryoneText,
            string cancelText);

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

        /// <summary>
        /// Hides any open login pairing ContentDialog (QR fullscreen, phone input, or
        /// pairing code). Safe no-op when none is showing. Call before leaving Login.
        /// </summary>
        void DismissLoginPairingDialogs();

        /// <summary>Opens the new-chat dialog bound to the supplied VM. Returns resolved JID or null.</summary>
        Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm);

        /// <summary>
        /// Group preview (avatar / title / creator / join). Returns true when the user joined.
        /// </summary>
        Task<bool> ShowGroupPreviewAsync(GroupPreviewDialogViewModel previewVm);

        /// <summary>
        /// Image send confirmation with preview and an optional caption box.
        /// <see cref="ImageSendPreviewResult.Confirmed"/> is false when cancelled.
        /// </summary>
        Task<ImageSendPreviewResult> ShowImageSendPreviewAsync(
            IReadOnlyList<byte[]> imageBytes,
            string infoText,
            string initialCaption = null);

        /// <summary>
        /// Document send confirmation: file name row (chat-info Files layout) plus caption.
        /// <see cref="ImageSendPreviewResult.Confirmed"/> is false when cancelled.
        /// </summary>
        Task<ImageSendPreviewResult> ShowDocumentSendPreviewAsync(
            string fileName,
            string infoText,
            string initialCaption = null);

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
        Task ShowWhatsAppSettingsAsync(SettingsViewModel settingsVm, bool openProfile = false);

        /// <summary>
        /// Read-only list of who reacted to <paramref name="messageVm"/>. No-op when the bubble has
        /// no reactions.
        /// </summary>
        Task ShowReactionsDialogAsync(ChatMessageViewModel messageVm);

        /// <summary>
        /// Mobile-friendly runtime health dialog (snapshot + recent journal + save to disk).
        /// </summary>
        Task ShowRuntimeDiagnosticsAsync();

        /// <summary>
        /// Picks the Start tile glyph. Returns null when the user cancels.
        /// The caller pins the tile only after a choice.
        /// </summary>
        Task<StartTileSelection> ShowStartTileMarkAsync(StartTileMark current, bool transparent);

        /// <summary>
        /// Picks the toast / badge audience (All / Groups / Contacts / Favorites).
        /// Returns null when the user cancels.
        /// </summary>
        Task<LockScreenNotificationScope?> ShowNotificationFilterAsync(LockScreenNotificationScope current);

        /// <summary>
        /// Pre-export: without media (primary). With-media is absent in this slice.
        /// Returns false when cancelled.
        /// </summary>
        Task<bool> ShowChatExportOptionsAsync();

        /// <summary>
        /// After export: file row (ChatInfo layout) with Share / Save. Returns the choice.
        /// </summary>
        Task<ChatExportReadyChoice> ShowChatExportReadyAsync(string fileName, string infoText);

        /// <summary>
        /// Status privacy (three audiences) bound to <paramref name="composer"/>. Choosing one of
        /// the two contact-list options closes the dialog with a Pick* result so the caller can
        /// open the contact picker and show the dialog again.
        /// </summary>
        Task<StatusPrivacyDialogChoice> ShowStatusPrivacyAsync(StatusComposerViewModel composer);

        /// <summary>
        /// Call-back confirm: title "Ligar para +CC DDD …", voice-call row + Cancel.
        /// Choosing voice call only dismisses the dialog (no dialer / empty command).
        /// </summary>
        Task ShowCallBackAsync(string phoneDigitsOrRaw);

        /// <summary>
        /// Disappearing-messages preset for create-group. Returns null when cancelled.
        /// </summary>
        Task<GroupEphemeralDuration?> ShowGroupEphemeralAsync(GroupEphemeralDuration current);
    }

    public enum StatusPrivacyDialogChoice
    {
        Cancel,
        Confirm,
        PickExcluded,
        PickOnlyShareWith
    }

    public enum ChatExportReadyChoice
    {
        Dismissed,
        Share,
        Save
    }
}
