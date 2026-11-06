// =============================================================================
// MessageRevocationContent
//
// Soft delete: flag IsRevoked and strip media chrome from the bubble, but keep
// Content / Caption so the UI can offer "view original". The italic "deleted"
// line is always UI-owned (localized), never written into Content.
// =============================================================================
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class MessageRevocationContent
    {
        public static void ApplyTombstone(ChatMessage target)
        {
            if (target == null)
            {
                return;
            }

            target.IsRevoked = true;
            // Soft delete — do not wipe Content / Caption (eye dialog).
            target.Kind = ChatMessageKind.Text;
            target.IsImage = false;
            target.ImageUri = null;
            target.ImageUrl = null;
            target.ImageDirectPath = null;
            target.ImageMediaKeyBase64 = null;
            target.ImageFileEncSha256Base64 = null;
            target.ImageMimeType = null;
            target.VideoUri = null;
            target.VideoPosterUri = null;
            target.VideoUrl = null;
            target.VideoDirectPath = null;
            target.VideoMediaKeyBase64 = null;
            target.VideoFileEncSha256Base64 = null;
            target.VideoMimeType = null;
            target.VideoDurationSeconds = 0;
            target.IsAnimated = false;
            target.IsAudio = false;
            target.AudioUri = null;
            target.AudioUrl = null;
            target.AudioDirectPath = null;
            target.AudioMediaKeyBase64 = null;
            target.AudioFileEncSha256Base64 = null;
            // Kind = Text clears IsGif / IsSticker / IsDocument / IsContact derived flags.
        }
    }
}
