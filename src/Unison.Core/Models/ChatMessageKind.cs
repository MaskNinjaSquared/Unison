namespace Unison.Core.Models
{
    /// <summary>
    /// Protocol-derived message type. Never inferred from preview text like "[Image]".
    /// </summary>
    public enum ChatMessageKind
    {
        Text = 0,
        Image = 1,
        Video = 2,
        Sticker = 3,
        Voice = 4,
        Audio = 5,
        Document = 6
    }
}
