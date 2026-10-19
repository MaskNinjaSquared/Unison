namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Dev-only tooling: watches local files (LocalState) for manually-triggered debug
    /// send requests. Started once by App in DEBUG builds; requests wait while the transport
    /// is not ready, so the connection does not have to start and stop it.
    /// </summary>
    public interface IDebugSendService
    {
        void Start();
        void Stop(string reason);
    }
}
