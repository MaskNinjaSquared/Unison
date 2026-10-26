namespace Unison.Core.Models
{
    /// <summary>
    /// What <see cref="Contracts.ICopyService.ReadPasteAsync"/> found on the clipboard.
    /// Video is intentionally omitted (no send path yet).
    /// </summary>
    public enum ClipboardPasteKind
    {
        None = 0,
        Text = 1,
        Image = 2,
        Audio = 3,
        Document = 4
    }
}
