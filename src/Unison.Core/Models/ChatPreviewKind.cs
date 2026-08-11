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
        Reaction = 6
    }
}
