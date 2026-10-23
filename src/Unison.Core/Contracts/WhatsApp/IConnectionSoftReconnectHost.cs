namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Transport primitives <see cref="IConnectionSoftReconnect"/> needs from the connection
    /// client. The façade owns the public soft-reconnect entry; the client runs the loops.
    /// </summary>
    public interface IConnectionSoftReconnectHost
    {
        /// <summary>Schedules the persistent reconnect loop (suppress/fatal/pairing gated).</summary>
        void ScheduleSoftReconnect(string reason);

        /// <summary>
        /// Soft-reconnect for another offline catch-up lot while the history banner stays up.
        /// </summary>
        void ScheduleCatchUpContinueReconnect(string reason);
    }
}
