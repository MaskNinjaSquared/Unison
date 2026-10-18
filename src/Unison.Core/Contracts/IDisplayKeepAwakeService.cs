namespace Unison.Core.Contracts
{
    /// <summary>
    /// WinRT <c>DisplayRequest</c> while the first history sync still holds the chat list
    /// (<c>x:Load</c> off). Released immediately before that hold ends so the list can materialize.
    /// </summary>
    public interface IDisplayKeepAwakeService
    {
        /// <summary>True while a display request is currently held.</summary>
        bool IsHeld { get; }

        /// <summary>Keep the screen on (idempotent). Call when the list enter first-sync hold.</summary>
        void Hold(string reason);

        /// <summary>
        /// Allow the screen to sleep again. Call immediately before clearing the list hold
        /// (the same turn that will flip <c>IsChatListMaterialized</c> / <c>x:Load</c>).
        /// </summary>
        void Release(string reason);
    }
}
