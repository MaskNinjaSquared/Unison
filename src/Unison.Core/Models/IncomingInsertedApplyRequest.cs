using System;
using Proto;

namespace Unison.Core.Models
{
    /// <summary>
    /// Post-<c>AcceptIncomingTimeline</c> insert follow-up: offline tip + persist, or live
    /// list apply + toast + persist. Media hydrate arms are optional.
    /// </summary>
    public sealed class IncomingInsertedApplyRequest
    {
        public string ChatJid { get; set; }
        public ChatMessage Message { get; set; }
        public string DisplayContent { get; set; }
        public string NotifyContent { get; set; }
        public bool IsOffline { get; set; }
        public bool IsGroup { get; set; }
        public bool IsFromMe { get; set; }
        public ChatPreviewKind PreviewKind { get; set; }
        public string ListAuthorPrefix { get; set; }
        public string StripParticipantJid { get; set; }
        public string SenderName { get; set; }
        public bool CountsAsUnread { get; set; }
        public bool SuppressToast { get; set; }
        public Message.Types.StickerMessage Sticker { get; set; }
        public Message.Types.ImageMessage Image { get; set; }
    }
}
