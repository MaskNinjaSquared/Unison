namespace Unison.Core.Contracts
{
    /// <summary>
    /// Lifetime of an inbound Share Target contract. Platform holds the WinRT
    /// <c>ShareOperation</c>; Core only signals when the UI is done so the host can
    /// call <c>ReportCompleted</c> without dismissing the window during activation.
    /// </summary>
    public interface IShareTargetSession
    {
        /// <summary>True while a Share Target operation is bound and not yet completed.</summary>
        bool IsActive { get; }

        /// <summary>
        /// Ends the Share Target contract. WinRT treats cancel the same as success here —
        /// call <see cref="Complete"/> (not a failure path) for: picker cancel, media
        /// apply done, or suspend while a share is bound. No-op when inactive.
        /// On Mobile this dismisses the share-host window — never during activation.
        /// </summary>
        void Complete();
    }
}
