using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Primary Live Tile surface (WinRT behind the adapter).
    /// Mirrors the Imgur/Giuraffe split: domain contract, platform implementation.
    /// </summary>
    public interface ILiveTilesService
    {
        void Initialize();

        /// <summary>
        /// Updates medium/wide/large bindings with the latest message preview.
        /// No-op when LiveTilesEnabled is off or the chat fails the lock-screen scope filter.
        /// </summary>
        void UpdateFromMessage(
            string title,
            string preview,
            string chatJid,
            int totalUnread,
            bool isGroup = false,
            bool isFavorite = false,
            bool isChannel = false);

        void Clear();

        /// <summary>Called when the user toggles Live Tiles in Settings.</summary>
        Task OnLiveTilesConfigChangedAsync();
    }
}
