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
        Document = 6,
        /// <summary>WhatsApp GIF (<c>VideoMessage.GifPlayback</c>) — video bytes, animation UI.</summary>
        Gif = 7,
        /// <summary>Shared contact card (<c>ContactMessage</c> / vCard).</summary>
        Contact = 8,
        /// <summary>
        /// Group system line: someone joined (<c>w:gp2</c> <c>add</c> / Baileys
        /// <c>GROUP_PARTICIPANT_ADD</c>). Not a protobuf body — notification stub.
        /// </summary>
        GroupJoin = 9
    }
}
