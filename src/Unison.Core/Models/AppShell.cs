namespace Unison.Core.Models
{
    /// <summary>
    /// Visual shell / chrome style the user can pick in Settings → Customization.
    /// Persisted as <see cref="Constants.LocalSettingsConstants.SelectedShell"/>.
    /// </summary>
    public enum AppShell
    {
        Unison = 0,
        WhatsApp = 1,
        /// <summary>Windows Phone 8–style shell: pivot + app bar, no sidebar.</summary>
        WhatsAppLegacy = 2
    }
}
