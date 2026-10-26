using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Ensure-connected entry the connection façade uses, plus the transport primitives
    /// <c>ConnectionEnsure</c> needs from the client.
    /// </summary>
    public interface IConnectionEnsureHost
    {
        /// <summary>
        /// Ensures a usable socket (healthy check, optional drop, connect with timeout).
        /// Serialized on the client's resume lock.
        /// </summary>
        Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false);

        /// <summary>True when the live socket still answers health checks.</summary>
        Task<bool> IsCurrentSocketHealthyAsync();

        /// <summary>Drops the current transport so the next connect opens a fresh one.</summary>
        void DropCurrentSocketForFastResume();

        /// <summary>True when credentials exist and pairing can be skipped.</summary>
        Task<bool> IsRegisteredAsync();

        /// <summary>Opens or replaces the socket (full connect path).</summary>
        Task ConnectAsync();

        /// <summary>True when the socket is up and usable right now.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Waits for the shared session-established gate up to <paramref name="timeoutMs"/>.
        /// Used when a parallel connect owns the socket.
        /// </summary>
        Task WaitForSessionEstablishedAsync(int timeoutMs);
    }
}
