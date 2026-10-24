namespace Unison.Core.Models
{
    /// <summary>
    /// Category of the chat-list last-message preview (not the full bubble content).
    /// </summary>
    public enum ChatPreviewKind
    {
        Text = 0,
        Image = 1,
        Video = 2,
        Sticker = 3,
        Voice = 4,
        Document = 5,
        Reaction = 6,
        Revoked = 7,
        /// <summary>WhatsApp GIF playback (video envelope with <c>GifPlayback</c>).</summary>
        Gif = 8,
        /// <summary>Shared contact card (vCard / <c>ContactMessage</c>).</summary>
        Contact = 9,
        /// <summary>Group join system line — strip shows plain text, no chip icon.</summary>
        GroupJoin = 10
    }
}
