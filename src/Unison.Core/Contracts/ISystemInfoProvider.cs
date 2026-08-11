namespace Unison.Core.Contracts
{
    /// <summary>
    /// Device / SKU helpers (Imgur <c>ISystemInfoProvider</c> pattern).
    /// </summary>
    public interface ISystemInfoProvider
    {
        /// <summary>True on Windows 10 Mobile (Windows.Mobile).</summary>
        bool IsMobile();

        /// <summary>
        /// Mobile Continuum (phone + mouse / docked desktop shell). Treat like desktop chrome.
        /// </summary>
        bool IsContinuum();
    }
}
