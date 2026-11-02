using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Notification-area icon on Desktop (full trust tray process in the desktop package).
    /// The tray only mirrors state and forwards "Exit"; the socket stays in the app process.
    /// </summary>
    public interface IDesktopTrayHost
    {
        /// <summary>
        /// Desktop with the full trust contract. True does not mean the tray exists: the plain
        /// package does not declare it, so <see cref="StartAsync"/> still returns false there.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>Launches the tray process. False when this package does not declare it.</summary>
        Task<bool> StartAsync();

        /// <summary>Total unread for the tray tooltip. Kept and resent when the tray connects.</summary>
        void PublishUnread(int totalUnread);

        /// <summary>
        /// Window close was cancelled (<c>confirmAppClose</c>): stub hides the frame window
        /// (close-to-tray) and keeps the icon. Pass the CoreWindow hwnd when known.
        /// </summary>
        void NotifyCloseRequested(long windowId);

        /// <summary>The user picked "Exit" in the tray menu.</summary>
        event EventHandler ExitRequested;
    }
}
