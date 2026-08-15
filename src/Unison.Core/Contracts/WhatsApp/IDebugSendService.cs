namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Dev-only tooling: watches local files (LocalState) for manually-triggered debug
    /// send requests. Only ever attached/started in DEBUG builds (see App.xaml.cs /
    /// WhatsAppService.AttachDebugSendService). Extracted out of WhatsAppService so the
    /// connection/session "client" doesn't carry test-only file-watching state.
    /// </summary>
    public interface IDebugSendService
    {
        void Start();
        void Stop(string reason);
    }
}
