using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionResume</c> asks of the connection client after the app becomes visible.
    /// </summary>
    public interface IConnectionResumeHost
    {
        Task<bool> IsRegisteredAsync();

        /// <summary>True when a fatal stream ended the session and resume must not reconnect.</summary>
        bool IsFatalSessionEnded { get; }

        /// <summary>Clears the suppress-reconnect latch so soft reconnect may run again.</summary>
        void ClearSuppressReconnect();

        /// <summary>
        /// Consumes a broker-owned reconnect request (dispose broker socket, clear noise).
        /// True when resume should force a fresh transport.
        /// </summary>
        Task<bool> ConsumeBrokerReconnectRequestAsync();

        /// <summary>Latches that the next ensure should drop the current transport first.</summary>
        void MarkForceFreshConnectOnResume();

        /// <summary>
        /// Reclaims a broker-owned live socket into the foreground. True when reclaim
        /// succeeded and resume is done (health monitor + pump already restarted).
        /// </summary>
        Task<bool> TryReclaimBrokerOwnedSocketAsync();

        /// <summary>Consumes and clears the force-fresh latch; returns the previous value.</summary>
        bool ConsumeForceFreshConnectOnResume();

        Task EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport);

        void RestartIncomingMessagePumpIfNeeded();

        void RequestSoftReconnect(string reason);

        void WriteResumeJournal(string eventName, string detail);

        void RecordResumeException(string eventName, System.Exception error);
    }
}
