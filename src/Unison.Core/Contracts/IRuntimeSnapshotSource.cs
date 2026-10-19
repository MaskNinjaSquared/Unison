using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Whoever holds the connection and sync counters the health journal samples. Today that is
    /// the client, which implements it explicitly; the journal depends on nothing else of it.
    /// </summary>
    public interface IRuntimeSnapshotSource
    {
        RuntimeDiagnosticsSnapshot CaptureRuntimeSnapshot();
    }
}
