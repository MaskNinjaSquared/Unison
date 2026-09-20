namespace Unison.Core.Contracts
{
    /// <summary>
    /// Recognises the "(You)" marker the app appends to the logged-in account's own name.
    /// </summary>
    /// <remarks>
    /// Behind an interface because the marker is localized, and reading resources is a
    /// platform concern. The rules that consume it are not.
    /// </remarks>
    public interface ISelfMarkerNaming
    {
        /// <summary>Whether the label is nothing but the marker — "(You)", "You", and translations.</summary>
        bool IsMarkerLabel(string label);

        /// <summary>The label without a trailing marker, or null when nothing is left.</summary>
        string StripMarker(string label);
    }
}
