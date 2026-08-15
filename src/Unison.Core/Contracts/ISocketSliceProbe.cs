using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Runs the new <c>Unison.Socket</c> stack end to end against the real servers, isolated on
    /// the debug surface. It exists so the rewritten handshake, pairing and query path can be
    /// proven before anything in the app depends on them.
    /// </summary>
    /// <remarks>
    /// The probe always runs on freshly generated credentials and its own transport, so it can
    /// neither read nor damage the signed-in session.
    /// </remarks>
    public interface ISocketSliceProbe
    {
        bool IsRunning { get; }

        /// <summary>Progress lines, already formatted for display.</summary>
        event EventHandler<string> Reported;

        /// <summary>Raised with the pairing QR payload so the host can render something scannable.</summary>
        event EventHandler<string> QrReceived;

        Task RunAsync();

        Task StopAsync();
    }
}
