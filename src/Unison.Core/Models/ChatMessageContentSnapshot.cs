namespace Unison.Core.Models
{
    /// <summary>
    /// Transport facts for message facade construction
    /// (<c>IMessageService.GetChatMessage</c> / <c>IChatMessageMapper.MapIndividual</c>).
    /// Prefer protocol flags; <see cref="Kind"/> is optional — the mapper resolves it when Text.
    /// </summary>
    public sealed class ChatMessageContentSnapshot
    {
        public string Text { get; set; }
        public ChatMessageKind Kind { get; set; }
        public bool IsImage { get; set; }
        public bool IsVideo { get; set; }
        public bool IsSticker { get; set; }
        public bool IsAudio { get; set; }
        public bool IsVoice { get; set; }
        public string Caption { get; set; }
    }
}
