namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Soft reconnect seams the history catch-up path needs without naming the whole client.
    /// </summary>
    /// <remarks>
    /// Implemented by <c>ConnectionFacade</c>. The connection client runs the loops behind
    /// <see cref="IConnectionSoftReconnectHost"/>.
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
