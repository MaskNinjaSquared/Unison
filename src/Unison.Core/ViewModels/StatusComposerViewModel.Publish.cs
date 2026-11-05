// =============================================================================
// StatusComposerViewModel.Publish
//
// Send: resolve the audience, publish through IStatusService, keep a dark
// "publishing" overlay up meanwhile. A failure drops the overlay and leaves
// the draft in place; success raises a toast and closes the composer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public sealed partial class StatusComposerViewModel
    {
        /// <summary>White text, the only text colour the composer offers.</summary>
        private const uint TextArgbWhite = 0xFFFFFFFF;

        /// <summary>First <see cref="BackgroundPalette"/> entry, used when a colour does not parse.</summary>
        private const uint DefaultBackgroundArgb = 0xFF128C7E;

        private bool _isPublishing;

        /// <summary>True while the Status is on its way (dark overlay + spinner).</summary>
        public bool IsPublishing
        {
            get => _isPublishing;
            private set
            {
                if (!Set(ref _isPublishing, value))
                {
                    return;
                }

                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PrivacyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private async Task PublishAsync()
        {
            if (!CanSend || _isPublishing)
            {
                return;
            }

            string title = _strings?.Get("StatusComposer_Title", "Text status") ?? "Text status";
            if (_status == null)
            {
                await ShowDialogAsync(
                    title,
                    _strings?.Get("StatusList_NewStatusUnavailable", "This option is not available yet.")
                        ?? "This option is not available yet.");
                return;
            }

            List<string> recipients = ResolveRecipients();
            if (recipients.Count == 0)
            {
                await ShowDialogAsync(
                    title,
                    _strings?.Get("StatusComposer_NoRecipients", "There are no contacts to share this status with.")
                        ?? "There are no contacts to share this status with.");
                return;
            }

            var post = new StatusTextPost
            {
                Text = _text.Trim(),
                BackgroundArgb = ParseArgb(_backgroundColor),
                TextArgb = TextArgbWhite,
                Font = FontWireTypes[_fontIndex],
                RecipientJids = recipients
            };

            CloseEmojiTray();
            IsPublishing = true;
            try
            {
                await _status.PublishTextStatusAsync(post);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusComposer] Publish failed: " + ex.Message);
                IsPublishing = false;
                await ShowDialogAsync(
                    _strings?.Get("StatusComposer_PublishFailedTitle", "Status not published") ?? "Status not published",
                    _strings?.Get(
                        "StatusComposer_PublishFailedBody",
                        "Your status could not be published. Check your connection and try again.")
                        ?? "Your status could not be published. Check your connection and try again.");
                return;
            }

            IsPublishing = false;
            _notifications?.ShowToast(
                _strings?.Get("StatusComposer_PublishedTitle", "Status published") ?? "Status published",
                _strings?.Get(
                    "StatusComposer_PublishedBody",
                    "Your status is now visible to the people you chose for the next 24 hours.")
                    ?? "Your status is now visible to the people you chose for the next 24 hours.");
            Text = string.Empty;
            Close();
        }

        /// <summary>"#AARRGGBB" palette entry to the ARGB integer WhatsApp expects.</summary>
        public static uint ParseArgb(string hex)
        {
            string digits = (hex ?? string.Empty).Trim().TrimStart('#');
            uint value;
            if (digits.Length == 6)
            {
                digits = "FF" + digits;
            }

            return digits.Length == 8 &&
                   uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                ? value
                : DefaultBackgroundArgb;
        }
    }
}
