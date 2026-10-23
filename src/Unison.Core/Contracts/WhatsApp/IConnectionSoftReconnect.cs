namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Soft reconnect seams the history catch-up path needs without naming the whole client.
    /// </summary>
    /// <remarks>
    /// Implemented by the connection client. Generic auto-reconnect and the catch-up continue
    /// path (banner stays up, short delay, then drop socket + reconnect) stay behind this port
    /// until ownership moves with Phase 4 wave 2.
    /// </remarks>
    public interface IConnectionSoftReconnect
    {
        /// <summary>
        /// Schedules the persistent reconnect loop when suppress/fatal/pairing gates allow it.
        /// </summary>
        void RequestSoftReconnect(string reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// </summary>
        void RequestCatchUpContinueReconnect(string reason);
    }
}
