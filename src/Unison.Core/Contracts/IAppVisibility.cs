namespace Unison.Core.Contracts
{
    /// <summary>
    /// Whether the app window is currently visible. Hosts report this; portable
    /// policy (toasts, catch-up) must not name <c>Window</c> / <c>App.IsWindowVisible</c>.
    /// </summary>
    public interface IAppVisibility
    {
        bool IsWindowVisible { get; }
    }
}
