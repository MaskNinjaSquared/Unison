using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Mobile status-bar progress (indeterminate "...") for sync/chrome text.
    /// No-op on desktop where StatusBar API is absent.
    /// </summary>
    public interface IStatusBarService
    {
        /// <summary>True when Windows.UI.ViewManagement.StatusBar is available.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Show indeterminate progress + optional label (portrait only; no-op in landscape).
        /// </summary>
        Task ShowProgressAsync(string text);

        /// <summary>
        /// Hide sync progress; restore "Unison" in portrait, or hide the bar in landscape.
        /// </summary>
        Task HideProgressAsync();
    }
}
