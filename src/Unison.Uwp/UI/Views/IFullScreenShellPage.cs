namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Page hosted in the full-screen Frame (<c>INavigator.NavigateFullScreen</c>). The shell asks
    /// it first on system Back; it closes itself through the navigator when it is done.
    /// </summary>
    internal interface IFullScreenShellPage
    {
        /// <summary>True when Back was consumed (inner state or closing the page).</summary>
        bool TryHandleBack();
    }
}
