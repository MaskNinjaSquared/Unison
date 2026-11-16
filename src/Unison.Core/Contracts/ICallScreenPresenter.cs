namespace Unison.Core.Contracts
{
    /// <summary>
    /// Shows the call screen over whatever shell theme is up while a call is tracked.
    /// </summary>
    public interface ICallScreenPresenter
    {
        /// <summary>Starts following the active call. Call once, on the UI thread.</summary>
        void Start();
    }
}
