namespace Unison.Core.Models
{
    /// <summary>
    /// List chrome for an author's Status ring: unviewed (Recent) vs already opened (Viewed).
    /// Local client state — not a wire receipt yet.
    /// </summary>
    public enum StatusViewState
    {
        /// <summary>At least one unexpired item has not been opened in the viewer.</summary>
        Recent = 0,

        /// <summary>Every unexpired item for this author was opened in the viewer.</summary>
        Viewed = 1
    }
}
