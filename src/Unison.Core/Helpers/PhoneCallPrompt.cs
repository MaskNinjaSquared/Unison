// =============================================================================
// PhoneCallPrompt
//
// Confirm, then open the system dialer via tel:. Used from chat detail and the
// profile info phone row so both surfaces ask the same question.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public static class PhoneCallPrompt
    {
        /// <summary>
        /// Asks whether to call <paramref name="displayTarget"/>, then launches <c>tel:+digits</c>.
        /// Returns true only when the dialer URI was accepted.
        /// </summary>
        public static async Task<bool> ConfirmAndCallAsync(
            string phoneDigitsOrRaw,
            string displayTarget,
            IDialogService dialogs,
            IUriLauncher launcher,
            IStringResources strings)
        {
            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneDigitsOrRaw);
            if (string.IsNullOrEmpty(digits) || launcher == null)
            {
                return false;
            }

            string label = string.IsNullOrWhiteSpace(displayTarget) ? digits : displayTarget.Trim();

            if (dialogs != null)
            {
                bool confirmed = await dialogs.ShowConfirmAsync(
                    title: Get(strings, "ChatDetail_CallPhoneTitle", "Start phone call?"),
                    content: string.Format(
                        Get(
                            strings,
                            "ChatDetail_CallPhoneBody",
                            "Do you want to start a phone call to {0}?"),
                        label),
                    primaryButtonText: Get(strings, "ChatDetail_CallPhoneConfirm", "Call"),
                    closeButtonText: Get(strings, "Common_Cancel", "Cancel"));

                if (!confirmed)
                {
                    return false;
                }
            }

            try
            {
                return await launcher.LaunchAsync("tel:+" + digits).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PhoneCallPrompt] Launch failed: " + ex.GetBaseException().Message);
                return false;
            }
        }

        private static string Get(IStringResources strings, string key, string fallback)
        {
            return strings == null ? fallback : strings.Get(key, fallback);
        }
    }
}
