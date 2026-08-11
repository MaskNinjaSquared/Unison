using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="Contracts.WhatsApp.IWhatsAppService.OnSessionCleared"/>.
    /// </summary>
    public sealed class SessionClearedEventArgs : EventArgs
    {
        public SessionClearedEventArgs(bool startPairing = true)
        {
            StartPairing = startPairing;
        }

        /// <summary>
        /// When false, shell shows Login surface without starting Connect/QR yet
        /// (auth wipe still in progress). When true, restart pairing immediately.
        /// </summary>
        public bool StartPairing { get; }
    }
}
